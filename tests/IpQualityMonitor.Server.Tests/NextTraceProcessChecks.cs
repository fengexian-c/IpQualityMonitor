using System.Net;
using TcpLatencyMonitor.Core;

internal static class NextTraceProcessChecks
{
    // Hermetic Python fixture: stdin/stdout only. It never imports a networking library.
    private const string Helper="""
#!/usr/bin/env python3
import json, os, sys, time
mode=__MODE__
with open(__PIDFILE__, 'w') as f: f.write(str(os.getpid()))
def emit(value):
    print(json.dumps(dict(schema=1, **value)), flush=True)
emit(dict(type='ready'))
for line in sys.stdin:
    request=json.loads(line)
    rid=request['id']; ip=request['ips'][0]
    if mode=='crash': sys.exit(3)
    if mode=='sleep':
        while True: time.sleep(1)
    if mode=='schema':
        print(json.dumps(dict(schema=2, type='result', id=rid, ip=ip)),flush=True);continue
    if mode=='oversize':
        print('x'*65537,flush=True);continue
    if mode=='error':
        emit(dict(type='error', id=rid, ip=ip, statusCode=429, retryAfter='120'));continue
    if mode=='provider-timeout':
        emit(dict(type='error', id=rid, ip=ip, reason='timeout'));continue
    if mode=='stderr':
        print('fixture-secret-should-never-escape',file=sys.stderr,flush=True)
        emit(dict(type='error', id=rid, ip=ip));continue
    if mode=='environment' and any(k.upper().startswith('NEXTTRACE_') for k in os.environ):
        emit(dict(type='error', id=rid, ip=ip));continue
    if mode=='wrong-id': rid='wrong-response-id'
    if mode=='wrong-ip': ip='9.9.9.9'
    result=dict(type='result', id=rid, ip=ip, geo=dict(ip=ip,country='US',city='Fixture'))
    if request['op']=='v4': result.update(statusCode=401,expiry='',retryAfter='')
    emit(result);emit(dict(type='done', id=rid, status='completed'))
""";
    public static async Task RunAsync(Action<bool,string> check)
    {
        if(OperatingSystem.IsWindows())return;
        string root=Path.Combine(Path.GetTempPath(),"iqm-process-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string Script(string mode)
            {
                string path=Path.Combine(root,mode),pid=path+".pid";
                File.WriteAllText(path,Helper.Replace("__MODE__",System.Text.Json.JsonSerializer.Serialize(mode),StringComparison.Ordinal).Replace("__PIDFILE__",System.Text.Json.JsonSerializer.Serialize(pid),StringComparison.Ordinal));
                File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);return path;
            }
            async Task<bool> Reaped(string script)
            {
                var pidFile=script+".pid";if(!File.Exists(pidFile))return true;
                int pid=int.Parse(File.ReadAllText(pidFile),System.Globalization.CultureInfo.InvariantCulture);
                for(int retry=0;retry<100&&Directory.Exists("/proc/"+pid);retry++)await Task.Delay(30);
                return !Directory.Exists("/proc/"+pid);
            }
            foreach(string mode in new[]{"wrong-id","wrong-ip","schema","oversize","crash"})
            {
                string script=Script(mode);using var client=new NextTraceProcessClient(script,TimeSpan.FromSeconds(2));
                bool rejected=false;try{await client.LookupAsync("8.8.8.8",CancellationToken.None);}catch(IOException){rejected=true;}
                check(rejected&&await Reaped(script),$"helper rejects {mode} protocol output and reaps its failed process");
            }
            {
                string script=Script("error");using var client=new NextTraceProcessClient(script,TimeSpan.FromSeconds(2));
                NextTraceProviderException? error=null;try{await client.LookupAsync("8.8.8.8",CancellationToken.None);}catch(NextTraceProviderException ex){error=ex;}
                check(error is {StatusCode:429,RetryAfter:"120"}&&await Reaped(script),"typed v3 status/Retry-After survives helper protocol without raw error content");
            }
            foreach(string mode in new[]{"sleep","provider-timeout"})
            {
                string script=Script(mode);using var client=new NextTraceProcessClient(script,TimeSpan.FromMilliseconds(mode=="sleep"?250:2000));
                bool timeout=false;try{await client.LookupAsync("8.8.8.8",CancellationToken.None);}catch(TimeoutException){timeout=true;}
                check(timeout&&await Reaped(script),$"{mode} produces an actual timeout and leaves no helper zombie");
            }
            {
                string script=Script("sleep");using var client=new NextTraceProcessClient(script,TimeSpan.FromSeconds(2));
                using var cancellation=new CancellationTokenSource(TimeSpan.FromMilliseconds(250));bool canceled=false;
                try{await client.LookupAsync("8.8.8.8",cancellation.Token);}catch(OperationCanceledException){canceled=true;}
                check(canceled&&await Reaped(script),"caller cancellation reaps helper and remains cancellation rather than timeout");
            }
            {
                string script=Script("stderr");using var client=new NextTraceProcessClient(script,TimeSpan.FromSeconds(2));string failure="";
                try{await client.LookupAsync("8.8.8.8",CancellationToken.None);}catch(IOException ex){failure=ex.ToString();}
                check(failure.Length>0&&!failure.Contains("fixture-secret",StringComparison.Ordinal)&&await Reaped(script),"helper stderr is drained but never copied into exceptions");
            }
            {
                const string variable="NEXTTRACE_FIXTURE_SECRET";string? old=Environment.GetEnvironmentVariable(variable);Environment.SetEnvironmentVariable(variable,"fixture-private");
                string script=Script("environment");
                try
                {
                    using(var client=new NextTraceProcessClient(script,TimeSpan.FromSeconds(2)))
                    {
                        using var response=await client.LookupV4Async("8.8.8.8","fixture-only-token",CancellationToken.None);
                        check(response.StatusCode==HttpStatusCode.Unauthorized,"v4 response status survives private stdin transport and strips inherited NEXTTRACE variables");
                    }
                    check(await Reaped(script),"disposing an idle helper reaps its process");
                }
                finally{Environment.SetEnvironmentVariable(variable,old);}
            }
        }
        finally{Directory.Delete(root,true);}
    }
}
