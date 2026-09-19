using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace TcpLatencyMonitor.Core;

/// <summary>
/// Optional serial analysis worker. Only one target's normalized universe/index is
/// retained here; inactive targets live in SQLite. Sampling owns a different queue.
/// Ordinary appends read new raw JSON only. Structural changes rebuild the affected
/// target from compact observations with the deterministic full-build oracle.
/// </summary>
public sealed class RouteAnalysisService : IAsyncDisposable
{
    private readonly History _history;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private readonly Channel<bool> _signal=Channel.CreateBounded<bool>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropWrite,SingleReader=true});
    private readonly ConcurrentDictionary<string,byte> _dirty=new();
    private readonly ConcurrentDictionary<(string Target,string Id),byte> _online=new();
    private readonly Task _worker;
    private string? _activeTarget,_version;
    private Dictionary<string,RouteObservation> _normalized=new();
    private Dictionary<string,string> _stamps=new();
    private RouteHistoryIndex? _index;
    private bool _initialized;
    private long _installed;
    private readonly long _sessionStarted=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long RawJsonParsed {get;private set;}
    public long Rebuilds {get;private set;}
    public long IncrementalAppends {get;private set;}
    public Exception? LastError {get;private set;}
    public event Action<string>? Updated;
    public event Action<Exception>? Failed;
    public RouteAnalysisService(History history,string path)
    {
        _history=history;Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString=new SqliteConnectionStringBuilder{DataSource=path,Pooling=true}.ToString();
        _history.RouteSourceCommitted+=SourceCommitted;_worker=Task.Run(WorkAsync);
    }
    private SqliteConnection Open()
    {
        var db=new SqliteConnection(_connectionString);db.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";cmd.ExecuteNonQuery();return db;
    }
    private void Initialize()
    {
        if(_initialized)return;
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS analysis_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS normalized_route(target TEXT NOT NULL,id TEXT NOT NULL,stamp TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(target,id));
            CREATE TABLE IF NOT EXISTS analysis_target(target TEXT PRIMARY KEY,version TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS first_decision(target TEXT NOT NULL,id TEXT NOT NULL,stamp TEXT NOT NULL,computed_ms INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(target,id,stamp));
            INSERT OR IGNORE INTO analysis_meta VALUES('installed_ms',CAST(CAST(unixepoch('subsec')*1000 AS INTEGER) AS TEXT));
            """;cmd.ExecuteNonQuery();
        cmd.CommandText="SELECT value FROM analysis_meta WHERE key='storage_version'";
        if(cmd.ExecuteScalar() is string storage&&storage!="1")throw new InvalidOperationException("路由分析数据库由其他版本创建；请使用对应版本。原始探测不受影响。");
        cmd.CommandText="INSERT OR IGNORE INTO analysis_meta VALUES('storage_version','1')";cmd.ExecuteNonQuery();
        cmd.CommandText="SELECT value FROM analysis_meta WHERE key='installed_ms'";_installed=long.Parse((string)cmd.ExecuteScalar()!,System.Globalization.CultureInfo.InvariantCulture);
        _history.InitializeRouteSource();_initialized=true;
    }
    private void SourceCommitted(string? target,string? route)
    {
        if(_stop.IsCancellationRequested)return;
        if(target is null)_dirty[""]=0;
        else{_dirty[target]=0;if(route is not null){if(_online.Count>=4096)_online.Clear();_online[(target,route)]=0;}}
        _signal.Writer.TryWrite(true);
    }
    private async Task WorkAsync()
    {
        // Startup/periodic ID reconciliation recovers notifications missed while the
        // app was closed, includes retention deletes, and never depends on this page.
        bool reconcile=true;
        while(!_stop.IsCancellationRequested)
        {
            try
            {
                await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    Initialize();
                    if(reconcile||_dirty.TryRemove("",out _))
                    {
                        foreach(var target in _history.RouteSourceTargets())_dirty[target]=0;
                        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT DISTINCT target FROM normalized_route UNION SELECT DISTINCT target FROM first_decision";
                        using var reader=cmd.ExecuteReader();while(reader.Read())_dirty[reader.GetString(0)]=0;
                    }
                    foreach(var target in _dirty.Keys.ToArray())
                    {
                        _stop.Token.ThrowIfCancellationRequested();if(!_dirty.TryRemove(target,out _)||target.Length==0)continue;
                        if(!NeedsBackgroundUpdate(target,_stop.Token))continue;
                        Sync(target,_stop.Token);Updated?.Invoke(target);
                    }
                }
                finally{_gate.Release();}
                LastError=null;
            }
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){break;}
            catch(Exception ex){LastError=ex;try{Failed?.Invoke(ex);}catch{}}
            try
            {
                using var wait=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);wait.CancelAfter(TimeSpan.FromMinutes(1));
                await _signal.Reader.ReadAsync(wait.Token).ConfigureAwait(false);reconcile=false;
            }
            catch(OperationCanceledException){reconcile=true;}
        }
    }
    private bool NeedsBackgroundUpdate(string target,CancellationToken token)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT version FROM analysis_target WHERE target=$t";cmd.Parameters.AddWithValue("$t",target);
        return cmd.ExecuteScalar() is not string stored||stored!=_history.RouteSourceVersion(target,token);
    }
    public async Task<RouteHistoryIndex> GetAsync(Target target,CancellationToken token=default)=>await GetAsync(target.Key,token).ConfigureAwait(false);
    public async Task<RouteHistoryIndex> GetAsync(string target,CancellationToken token=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,_stop.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await Task.Run(()=>{Initialize();return Sync(target,linked.Token);},linked.Token).ConfigureAwait(false);
        }
        catch(Exception ex)when(ex is not OperationCanceledException){LastError=ex;throw;}
        finally{_gate.Release();}
    }
    private void LoadNormalized(SqliteConnection db,string target,CancellationToken token)
    {
        var normalized=new Dictionary<string,RouteObservation>();var stamps=new Dictionary<string,string>();
        using var cmd=db.CreateCommand();cmd.CommandText="SELECT id,stamp,json FROM normalized_route WHERE target=$t";cmd.Parameters.AddWithValue("$t",target);
        using var reader=cmd.ExecuteReader();while(reader.Read())
        {
            token.ThrowIfCancellationRequested();string id=reader.GetString(0);stamps[id]=reader.GetString(1);
            normalized[id]=JsonSerializer.Deserialize(reader.GetString(2),DataJson.Default.RouteObservation)!;
        }
        _activeTarget=target;_version=null;_index=null;_normalized=normalized;_stamps=stamps;
    }
    private RouteHistoryIndex Sync(string target,CancellationToken token)
    {
        using var db=Open();if(_activeTarget!=target)LoadNormalized(db,target,token);
        var delta=_history.ReadRouteSource(target,_stamps,token);RawJsonParsed+=delta.Changed.Count;
        if(_index is not null&&_version==delta.Version)return _index;
        var entries=delta.Entries.ToDictionary(e=>e.Id);var next=new Dictionary<string,RouteObservation>(_normalized);
        foreach(var id in next.Keys.Where(id=>!entries.ContainsKey(id)).ToArray())next.Remove(id);
        foreach(var observation in delta.Changed)next[observation.Id]=observation;
        RouteHistoryIndex? index=null;
        if(_index is not null&&next.Count==_normalized.Count+delta.Changed.Count&&delta.Changed.All(o=>!_normalized.ContainsKey(o.Id)))index=_index.TryAppendUnchanged(delta.Changed,delta.Boundaries,token);
        if(index is null){index=RouteHistoryIndex.Build(next.Values,token,delta.Boundaries);Rebuilds++;}else IncrementalAppends++;
        var decisions=ReadDecisions(db,target,entries,token);
        using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.Parameters.AddWithValue("$t",target);var idParam=cmd.Parameters.Add("$id",SqliteType.Text);var stampParam=cmd.Parameters.Add("$stamp",SqliteType.Text);var jsonParam=cmd.Parameters.Add("$json",SqliteType.Text);
        foreach(var id in _stamps.Keys.Where(id=>!entries.ContainsKey(id)))
        {token.ThrowIfCancellationRequested();cmd.CommandText="DELETE FROM normalized_route WHERE target=$t AND id=$id";idParam.Value=id;cmd.ExecuteNonQuery();}
        foreach(var observation in delta.Changed)
        {
            token.ThrowIfCancellationRequested();idParam.Value=observation.Id;stampParam.Value=entries[observation.Id].Stamp;jsonParam.Value=JsonSerializer.Serialize(observation,DataJson.Default.RouteObservation);
            cmd.CommandText="INSERT OR REPLACE INTO normalized_route VALUES($t,$id,$stamp,$json)";cmd.ExecuteNonQuery();
        }
        var computedParam=cmd.Parameters.Add("$computed",SqliteType.Integer);
        foreach(var observation in index.Observations)
        {
            token.ThrowIfCancellationRequested();if(decisions.ContainsKey(observation.Id))continue;
            var entry=entries[observation.Id];bool online=_online.ContainsKey((target,observation.Id))||entry.SeenMs>_sessionStarted;
            // Zero marks records present before the revision observer was installed.
            // They never acquire a fabricated historical first judgement.
            if(!online&&(entry.SeenMs==0||entry.SeenMs<_installed))continue;
            var causal=observation.Id==index.Observations.LastOrDefault()?.Id?index:
                RouteHistoryIndex.Build(index.Observations.Where(o=>o.Finished<=observation.Finished),token,delta.Boundaries.Where(b=>b<=observation.Finished));
            var member=causal.Memberships[observation.Id];var pattern=causal.Patterns.FirstOrDefault(p=>p.Id==member.PatternId);
            var decision=new RouteFirstDecision(observation.Id,observation.ExecutionId,target,observation.Scope,RouteHistoryIndex.AlgorithmVersion,causal.Revision,
                DateTimeOffset.UtcNow,observation.Finished,member.PatternId,pattern?.Description,member.Kind,member.EvidenceId,online?"在线首次判断":"重启补算"){SourceStamp=entry.Stamp};
            idParam.Value=observation.Id;stampParam.Value=entry.Stamp;computedParam.Value=decision.ComputedAt.ToUnixTimeMilliseconds();jsonParam.Value=JsonSerializer.Serialize(decision,DataJson.Default.RouteFirstDecision);
            cmd.CommandText="INSERT OR IGNORE INTO first_decision VALUES($t,$id,$stamp,$computed,$json)";cmd.ExecuteNonQuery();decisions[observation.Id]=decision;
        }
        // Audit facts are not removed by cache rebuild. Normal retention removes
        // only facts whose source is absent and whose computation is >31 days old.
        cmd.CommandText="DELETE FROM first_decision WHERE target=$t AND computed_ms < $cutoff AND NOT EXISTS(SELECT 1 FROM normalized_route n WHERE n.target=first_decision.target AND n.id=first_decision.id AND n.stamp=first_decision.stamp)";
        cmd.Parameters.AddWithValue("$cutoff",DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeMilliseconds());cmd.ExecuteNonQuery();
        cmd.CommandText="INSERT OR REPLACE INTO analysis_target VALUES($t,$version)";cmd.Parameters.AddWithValue("$version",delta.Version);cmd.ExecuteNonQuery();
        token.ThrowIfCancellationRequested();tx.Commit();
        // Publish atomically after the derived transaction commits. Cancellation or
        // disk failure leaves the old projection intact and raw writes untouched.
        _normalized=next;_stamps=entries.ToDictionary(p=>p.Key,p=>p.Value.Stamp);_index=index;_version=delta.Version;
        index.FirstDecisions=decisions;index.SourceVersion=delta.Version;
        // A newer notification can arrive while this snapshot is being computed.
        // Consume only IDs actually present in the committed analysis transaction.
        foreach(var id in entries.Keys)_online.TryRemove((target,id),out _);
        return index;
    }
    private static Dictionary<string,RouteFirstDecision> ReadDecisions(SqliteConnection db,string target,Dictionary<string,RouteSourceEntry> entries,CancellationToken token)
    {
        var result=new Dictionary<string,RouteFirstDecision>();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id,stamp,json FROM first_decision WHERE target=$t";cmd.Parameters.AddWithValue("$t",target);
        using var reader=cmd.ExecuteReader();while(reader.Read())
        {
            token.ThrowIfCancellationRequested();string id=reader.GetString(0);
            if(entries.TryGetValue(id,out var entry)&&entry.Stamp==reader.GetString(1))result[id]=JsonSerializer.Deserialize(reader.GetString(2),DataJson.Default.RouteFirstDecision)!;
        }
        return result;
    }
    public async ValueTask DisposeAsync()
    {
        _history.RouteSourceCommitted-=SourceCommitted;_stop.Cancel();_signal.Writer.TryComplete();
        await _worker.ConfigureAwait(false);await _gate.WaitAsync().ConfigureAwait(false);_gate.Release();_stop.Dispose();
    }
}
