using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TcpLatencyMonitor.Core;

// One on-demand process per shared client. Credentials travel only over private stdin.
public sealed class NextTraceProcessClient:INextTraceLookup,INextTraceV4Lookup
{
    private readonly string _path;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly object _sync=new();
    private Process? _process;
    private Timer? _idle;
    private bool _disposed;
    private long _sequence;
    private long _idleGeneration;
    public NextTraceProcessClient(string path,TimeSpan? requestTimeout=null)
    {
        _path=Path.GetFullPath(path);_requestTimeout=requestTimeout??TimeSpan.FromSeconds(18);
        if(_requestTimeout<=TimeSpan.Zero||_requestTimeout>TimeSpan.FromMinutes(2))throw new ArgumentOutOfRangeException(nameof(requestTimeout));
    }
    public async Task<string> LookupAsync(string address,CancellationToken token)
    {
        using var doc=JsonDocument.Parse(await ExchangeAsync(address,"lookup",null,token).ConfigureAwait(false));
        return doc.RootElement.GetProperty("geo").GetRawText();
    }
    public async Task<HttpResponseMessage> LookupV4Async(string address,string credential,CancellationToken token)
    {
        if(credential.Length is 0 or >4096||credential.Any(char.IsControl))throw new ArgumentException("Token 格式无效。");
        using var doc=JsonDocument.Parse(await ExchangeAsync(address,"v4",credential,token).ConfigureAwait(false));var e=doc.RootElement;
        int code=e.GetProperty("statusCode").GetInt32();if(code is <100 or >599)throw new IOException("定位 HTTP 状态无效");
        var response=new HttpResponseMessage((System.Net.HttpStatusCode)code){Content=new StringContent(e.TryGetProperty("geo",out var geo)?geo.GetRawText():"")};
        if(e.TryGetProperty("expiry",out var expiry)&&expiry.GetString() is {Length:>0} stamp)response.Headers.TryAddWithoutValidation("X-NextTrace-Quota-Expires-At",stamp);
        if(e.TryGetProperty("retryAfter",out var retry)&&retry.GetString() is {Length:>0} delay)response.Headers.TryAddWithoutValidation("Retry-After",delay);
        return response;
    }
    private async Task<string> ExchangeAsync(string address,string operation,string? credential,CancellationToken token)
    {
        if(NodeMetadataClient.LocalLabel(address) is not null)throw new ArgumentException("Only public IPs are accepted");
        address=CidrBlock.Normalize(address);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(_requestTimeout);
        try
        {
            Process process;bool fresh;
            lock(_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed,this);_idleGeneration++;_idle?.Dispose();_idle=null;
                fresh=_process is null||_process.HasExited;
                if(fresh)
                {
                    StopLocked();
                    var start=new ProcessStartInfo(_path){WorkingDirectory=Path.GetDirectoryName(_path)!,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
                        StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
                    start.ArgumentList.Add("--live");start.ArgumentList.Add("--lifetime=10m");
                    foreach(var key in start.Environment.Keys.Where(k=>k.StartsWith("NEXTTRACE_",StringComparison.OrdinalIgnoreCase)).ToArray())start.Environment.Remove(key);
                    _process=Process.Start(start)??throw new IOException("无法启动 NextTrace 定位组件");
                    _=DrainAsync(_process.StandardError);
                }
                process=_process!;
            }
            // Killing the private process also interrupts blocked stdout reads and PoW.
            using var registration=deadline.Token.Register(()=>{lock(_sync){if(ReferenceEquals(_process,process))StopLocked();}});
            if(fresh)
            {
                using var ready=await ReadAsync(process,deadline.Token).ConfigureAwait(false);
                if(ready.RootElement.GetProperty("type").GetString()!="ready")throw new IOException("定位组件初始化响应无效");
            }
            string id=Interlocked.Increment(ref _sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var requestBytes=new MemoryStream();
            using(var writer=new Utf8JsonWriter(requestBytes))
            {
                writer.WriteStartObject();writer.WriteString("id",id);writer.WriteString("op",operation);writer.WriteStartArray("ips");writer.WriteStringValue(address);writer.WriteEndArray();
                if(credential is not null)writer.WriteString("token",credential);writer.WriteEndObject();
            }
            string request=Encoding.UTF8.GetString(requestBytes.GetBuffer(),0,(int)requestBytes.Length);
            await process.StandardInput.WriteLineAsync(request.AsMemory(),deadline.Token).ConfigureAwait(false);
            Array.Clear(requestBytes.GetBuffer());request="";
            await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            string? result=null;
            for(int messages=0;messages<12;messages++)
            {
                using var doc=await ReadAsync(process,deadline.Token).ConfigureAwait(false);var e=doc.RootElement;
                if(!e.TryGetProperty("id",out var responseId)||responseId.GetString()!=id)throw new IOException("定位组件响应序号无效");
                string type=e.GetProperty("type").GetString()??"";
                if(type=="error")
                {
                    if(e.TryGetProperty("statusCode",out var status)&&status.TryGetInt32(out int code)&&code is >=400 and <=599)
                        throw new NextTraceProviderException(code,e.TryGetProperty("retryAfter",out var retry)&&retry.ValueKind==JsonValueKind.String?retry.GetString():null);
                    if(e.TryGetProperty("reason",out var reason)&&reason.GetString()=="timeout")throw new TimeoutException("NextTrace provider request timed out");
                    throw new IOException("定位组件未完成查询");
                }
                if(type=="result")
                {
                    if(e.GetProperty("ip").GetString()!=address)throw new IOException("定位组件响应地址无效");
                    result=e.GetRawText();
                }
                if(type=="done")
                {
                    if(result is null||e.GetProperty("status").GetString()!="completed")throw new IOException("定位组件未返回有效结果");
                    deadline.Token.ThrowIfCancellationRequested();
                    lock(_sync)if(!_disposed&&ReferenceEquals(_process,process))
                    {
                        long generation=_idleGeneration;
                        _idle=new Timer(_=>{lock(_sync)if(generation==_idleGeneration&&ReferenceEquals(_process,process))StopLocked();},null,TimeSpan.FromMinutes(2),Timeout.InfiniteTimeSpan);
                    }
                    return result;
                }
            }
            throw new IOException("定位组件响应数量异常");
        }
        catch{lock(_sync)StopLocked();token.ThrowIfCancellationRequested();if(deadline.IsCancellationRequested)throw new TimeoutException("NextTrace helper request timed out");throw;}
        finally{_gate.Release();}
    }
    private static async Task<JsonDocument> ReadAsync(Process process,CancellationToken token)
    {
        // Enforce the line limit while reading, not after allocating an unbounded line.
        var line=new StringBuilder(2048);var character=new char[1];
        while(true)
        {
            int count=await process.StandardOutput.ReadAsync(character.AsMemory(),token).ConfigureAwait(false);
            if(count==0)throw new IOException("定位组件响应缺失");
            if(character[0]=='\n')break;
            if(line.Length>=65536)throw new IOException("定位组件响应过大");
            line.Append(character[0]);
        }
        var doc=JsonDocument.Parse(line.ToString());
        if(!doc.RootElement.TryGetProperty("schema",out var schema)||schema.GetInt32()!=1){doc.Dispose();throw new IOException("定位组件协议版本不支持");}
        return doc;
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        try{var buffer=new char[4096];while(await reader.ReadAsync(buffer).ConfigureAwait(false)>0){}}
        catch(Exception e) when(e is IOException or ObjectDisposedException){}
        // Upstream logs can contain HTTP bodies. Never store their text or include it in errors.
    }
    private void StopLocked()
    {
        _idleGeneration++;_idle?.Dispose();_idle=null;var old=_process;_process=null;
        if(old is null)return;
        try{if(!old.HasExited)old.Kill(entireProcessTree:true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
        // Readers may be unwinding due to cancellation; their process reference remains valid.
        _=Task.Run(async()=>{try{await old.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);}catch{}finally{old.Dispose();}});
    }
    public void Dispose(){lock(_sync){_disposed=true;StopLocked();}}
}

