using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TcpLatencyMonitor.Core;

internal static class AnnotationStorageChecks
{
    public static async Task RunAsync(Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(Path.GetTempPath(),"iqm-annotation-storage-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await Persistence(Path.Combine(folder,"history.db"),check);
            await Recovery(Path.Combine(folder,"recovery.db"),check,reject);
            await MetadataIsolation(Path.Combine(folder,"metadata.db"),check);
            await DiskFailure(Path.Combine(folder,"failure.db"),check);
        }
        finally{SqliteConnection.ClearAllPools();Directory.Delete(folder,true);}
    }
    private static RouteRun Route(string id,Target target,DateTimeOffset time)=>new(id,target.Key,target.Address,"fixture",time,time,
        "fixture","partial",false,500,32,[new(1,1,"1.1.1.1",11013,1)]);
    private static RouteAnnotation Annotation(RouteRun route,DateTimeOffset now,bool complete=false)
    {
        var metadata=new Dictionary<string,NodeMetadata>();
        if(complete)metadata["1.1.1.1"]=new("1.1.1.1",true,13335,"fixture","fixture","US","CA","Los Angeles",now,now.AddDays(7),"");
        return RouteClassifier.Classify(route,metadata,now,"fixture");
    }
    private static object? Sql(string path,string statement,params (string Name,object Value)[] parameters)
    {
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path}.ToString());db.Open();
        using var cmd=db.CreateCommand();cmd.CommandText=statement;
        foreach(var parameter in parameters)cmd.Parameters.AddWithValue(parameter.Name,parameter.Value);
        return cmd.ExecuteScalar();
    }
    private static async Task Persistence(string path,Action<bool,string> check)
    {
        var now=DateTimeOffset.UtcNow;var target=Target.Parse("8.8.8.8",0,ProbeProtocol.Icmp,500);
        await using var history=new History(path);history.Initialize();
        var route=Route("persisted",target,now);history.SaveRoute(route);
        long batches=history.CommittedBatches;
        check(history.LoadRouteAnnotation(route.Id) is null&&history.CommittedBatches==batches,
            "reading a missing annotation performs no derived writes");
        var annotation=Annotation(route,now);var saved=history.SaveRouteAnnotation(annotation);
        check(saved?.Id==annotation.Id,"annotation save returns the revision actually persisted");
        string original=(string)Sql(path,"SELECT json FROM route_annotation WHERE id=$id",("$id",annotation.Id))!;
        var duplicate=history.SaveRouteAnnotation(annotation with{Id="duplicate",Time=now.AddSeconds(1)});
        check(duplicate?.Id==annotation.Id&&(long)Sql(path,"SELECT COUNT(*) FROM route_annotation")! == 1,
            "identical evidence returns its existing persisted revision without another row");
        var revised=Annotation(route,now.AddSeconds(2),complete:true);history.SaveRouteAnnotation(revised);
        batches=history.CommittedBatches;
        check(history.LoadRouteAnnotation(route.Id,true)?.Id==annotation.Id&&history.LoadRouteAnnotation(route.Id)?.Id==revised.Id&&
            history.CommittedBatches==batches&&(string)Sql(path,"SELECT json FROM route_annotation WHERE id=$id",("$id",annotation.Id))! == original,
            "original and latest reads preserve the frozen original JSON without writes");
        Sql(path,"DELETE FROM route_run WHERE id=$id",("$id",route.Id));
        check(history.SaveRouteAnnotation(revised) is null&&history.SaveRouteAnnotation(revised with{Id="deleted-revision",EvidenceKey="new"}) is null&&
            history.LoadRouteAnnotation(route.Id) is null&&(long)Sql(path,"SELECT COUNT(*) FROM route_annotation")! == 2,
            "a deleted raw route cannot report a new or deduplicated annotation as saved");

        var live=Route("live",target,now.AddSeconds(-1));history.SaveRoute(live);
        var valid=Annotation(live,now);history.SaveRouteAnnotation(valid);
        int failures=0;history.OptionalWriteFailed+=_=>Interlocked.Increment(ref failures);
        var conflicting=valid with{EvidenceKey="different-evidence",Time=now.AddSeconds(3)};
        await history.RecordAsync(()=>
        {
            history.Add(target,new(now,ProbeStatus.Success,1,1,"before optional failure"));
            check(history.SaveRouteAnnotation(conflicting) is null,"nested rejected annotation does not pretend to persist");
            history.Add(target,new(now,ProbeStatus.Success,2,2,"after optional failure"));
        });
        var writes=Enumerable.Range(0,24).Select(i=>Task.Run(()=>
        {
            if(i%2==0){history.Add(target,new(now,ProbeStatus.Success,i,i,"concurrent fixture"));return true;}
            return history.SaveRouteAnnotation(conflicting) is null;
        })).ToArray();
        check((await Task.WhenAll(writes)).All(result=>result),"optional constraint failures stay isolated from neighboring samples");
        check(history.WriteFailure is null&&failures==13&&(long)Sql(path,"SELECT COUNT(*) FROM sample")! == 14,
            "optional constraint failures do not close the measurement writer or lose samples");
        check(history.SaveRouteAnnotation(valid with{Id=""}) is null&&history.WriteFailure is null&&failures==14,
            "invalid optional annotation data is diagnosed without poisoning sampling");
        Sql(path,"INSERT INTO route_annotation VALUES('malformed',$route,$time,'{broken')",("$route",live.Id),("$time",now.AddSeconds(4).ToUnixTimeMilliseconds()));
        batches=history.CommittedBatches;
        check(history.LoadRouteAnnotation(live.Id) is null&&history.CommittedBatches==batches,
            "malformed optional JSON reads as unavailable without a repair write");
        var repair=Annotation(live,now.AddSeconds(5));
        check(history.SaveRouteAnnotation(repair)?.Id==repair.Id&&history.WriteFailure is null,
            "explicit recovery can append a valid revision after malformed optional JSON");

        // Inject a JSON failure after a write to verify rollback itself, not merely
        // a failed single INSERT. No external provider, filesystem fault or probe runs.
        var optional=typeof(History).GetMethod("TryWriteOptional",BindingFlags.NonPublic|BindingFlags.Instance)!;
        bool Inject()=> (bool)optional.Invoke(history,[new Action<SqliteConnection>(db=>
        {
            using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO metadata_state VALUES('must-rollback',1)";cmd.ExecuteNonQuery();
            throw new JsonException("fixture optional parse failure");
        })])!;
        check(!Inject()&&(long)Sql(path,"SELECT COUNT(*) FROM metadata_state WHERE name='must-rollback'")! == 0&&history.WriteFailure is null,
            "standalone optional parse failure rolls back its own transaction");
        await history.RecordAsync(()=>
        {
            history.Add(target,new(now,ProbeStatus.Success,3,3,"nested parse fixture"));
            check(!Inject(),"nested optional parse failure is reported as rejected");
        });
        check((long)Sql(path,"SELECT COUNT(*) FROM metadata_state WHERE name='must-rollback'")! == 0&&
            (long)Sql(path,"SELECT COUNT(*) FROM sample")! == 15&&history.WriteFailure is null,
            "nested optional parse failure rolls back its savepoint while committing the sample");
    }
    private static async Task Recovery(string path,Action<bool,string> check,Action<Action,string> reject)
    {
        var now=DateTimeOffset.UtcNow;var target=Target.Parse("8.8.8.8",0,ProbeProtocol.Icmp,500);
        var other=Target.Parse("9.9.9.9",0,ProbeProtocol.Icmp,500);
        await using var history=new History(path);history.Initialize();
        var missing=Route("missing",target,now.AddMinutes(-1));
        var incomplete=Route("incomplete",target,now.AddMinutes(-2));
        var complete=Route("complete",target,now.AddMinutes(-3));
        var malformed=Route("malformed",target,now.AddMinutes(-4));
        var old=Route("old",target,now.AddDays(-2));
        var expired=Route("expired",target,now.AddDays(-32));
        var removed=Route("removed",target,now.AddMinutes(-5));
        foreach(var route in new[]{missing,incomplete,complete,malformed,old,expired,removed,Route("other",other,now),Route("future",target,now.AddMinutes(1))})history.SaveRoute(route);
        history.SaveRouteAnnotation(Annotation(incomplete,now));history.SaveRouteAnnotation(Annotation(complete,now,complete:true));
        Sql(path,"INSERT INTO route_annotation VALUES('bad',$route,$time,'{broken')",("$route",malformed.Id),("$time",now.ToUnixTimeMilliseconds()));
        Sql(path,"DELETE FROM route_run WHERE id='removed'");
        long batches=history.CommittedBatches;
        var offline=history.LoadRoutesNeedingAnnotation(now,[target.Key],includeIncomplete:false).Select(r=>r.Id).ToArray();
        var online=history.LoadRoutesNeedingAnnotation(now,[target.Key],includeIncomplete:true).Select(r=>r.Id).ToArray();
        check(offline.SequenceEqual(new[]{"missing","malformed"})&&online.SequenceEqual(new[]{"missing","incomplete","malformed"}),
            "startup recovery separates missing snapshots from incomplete online evidence");
        check(history.CommittedBatches==batches&&history.LoadRoutesNeedingAnnotation(now,[],true).Count==0,
            "recovery reads cannot write or include removed configured targets");
        var retained=history.LoadRoutesNeedingAnnotation(now,[target.Key],true,lookback:TimeSpan.FromDays(31)).Select(r=>r.Id).ToArray();
        check(retained.Contains("old")&&!retained.Contains("expired")&&!retained.Contains("removed")&&!retained.Contains("future")&&!retained.Contains("other"),
            "recovery excludes retention-expired, deleted, future and other-target raw routes");
        check(history.LoadRoutesNeedingAnnotation(now,[target.Key],false,limit:2).Select(r=>r.Id).SequenceEqual(new[]{"missing"}),
            "recovery caps raw rows examined before filtering optional annotation state");
        reject(()=>history.LoadRoutesNeedingAnnotation(now,[target.Key],true,limit:2049),"recovery refuses an unbounded row limit");
        reject(()=>history.LoadRoutesNeedingAnnotation(now,[target.Key],true,lookback:TimeSpan.FromDays(32)),"recovery refuses a window beyond retention");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        reject(()=>history.LoadRoutesNeedingAnnotation(now,[target.Key],true,token:cancelled.Token),"recovery honors cancellation before reading");
        var expiring=Annotation(complete,now,complete:true);
        expiring=expiring with{Time=now.AddSeconds(1),EvidenceKey="expires-soon",Nodes=expiring.Nodes.Select(node=>node with{Metadata=node.Metadata! with{Expires=now.AddMinutes(1)}}).ToList()};
        history.SaveRouteAnnotation(expiring);
        check(!history.LoadRoutesNeedingAnnotation(now,[target.Key],true).Any(route=>route.Id==complete.Id)&&
            history.LoadRoutesNeedingAnnotation(now.AddMinutes(2),[target.Key],true).Any(route=>route.Id==complete.Id),
            "previously complete annotations re-enter recovery when their frozen public metadata expires");
        var aged=expiring with{Id="aged",Time=now.AddSeconds(2),EvidenceKey="refresh-policy",Nodes=expiring.Nodes.Select(node=>node with{Metadata=node.Metadata! with{Queried=now.AddHours(-2),Expires=now.AddDays(1)}}).ToList()};
        history.SaveRouteAnnotation(aged);
        check(!history.LoadRoutesNeedingAnnotation(now,[target.Key],true,refreshHours:24).Any(route=>route.Id==complete.Id)&&
            history.LoadRoutesNeedingAnnotation(now,[target.Key],true,refreshHours:1).Any(route=>route.Id==complete.Id),
            "recovery honors the configured refresh interval even before provider expiry");
        reject(()=>history.LoadRoutesNeedingAnnotation(now,[target.Key],true,refreshHours:0),"recovery validates metadata refresh policy bounds");
        var raw=(string)Sql(path,"SELECT json FROM route_run WHERE id='missing'")!;
        foreach(var route in history.LoadRoutesNeedingAnnotation(now,[target.Key],false))history.SaveRouteAnnotation(Annotation(route,now));
        check(history.LoadRoutesNeedingAnnotation(now,[target.Key],false).Count==0&&(string)Sql(path,"SELECT json FROM route_run WHERE id='missing'")! == raw,
            "bounded recovery fills missing snapshots without changing raw probe JSON");
    }
    private static async Task MetadataIsolation(string path,Action<bool,string> check)
    {
        var now=DateTimeOffset.UtcNow;var target=Target.Parse("8.8.8.8",0,ProbeProtocol.Icmp,500);
        await using var history=new History(path);history.Initialize();int failures=0;history.OptionalWriteFailed+=_=>Interlocked.Increment(ref failures);
        var metadata=new NodeMetadata("1.1.1.1",true,13335,"fixture","fixture","US","CA","Los Angeles",now,now.AddDays(7),""){ProviderId="nexttrace"};
        // A previously damaged provider row must not cause saving healthy evidence
        // from a second provider to roll back the independent measurement batch.
        Sql(path,"INSERT INTO geo_observation VALUES('broken','1.1.1.1',$expiry,'{broken','{broken')",("$expiry",now.AddDays(1).ToUnixTimeMilliseconds()));
        await history.RecordAsync(()=>
        {
            history.Add(target,new(now,ProbeStatus.Success,1,1,"before malformed metadata"));
            check(!history.SaveNodeMetadata(metadata),"a malformed other-provider observation rejects only the optional combined write");
            history.Add(target,new(now,ProbeStatus.Success,2,2,"after malformed metadata"));
        });
        check(history.WriteFailure is null&&failures==1&&(long)Sql(path,"SELECT COUNT(*) FROM sample")! == 2&&
            (long)Sql(path,"SELECT COUNT(*) FROM geo_observation WHERE provider='nexttrace'")! == 0,
            "mixed-provider JSON failure rolls back optional observation changes while preserving both samples");
        check(!history.RefreshCombinedMetadata("1.1.1.1")&&history.WriteFailure is null,
            "explicit combined-cache rebuild isolates malformed optional JSON");
        Sql(path,"UPDATE geo_observation SET success_json='{}',attempt_json='{}' WHERE provider='broken'");
        check(!history.SaveNodeMetadata(metadata)&&history.WriteFailure is null,
            "missing required provider fields are treated as optional data errors rather than fatal null dereferences");
        Sql(path,"DELETE FROM geo_observation WHERE provider='broken'");
        check(history.SaveNodeMetadata(metadata)&&history.LoadProviderMetadata("1.1.1.1","nexttrace") is {Success:true},
            "healthy metadata saves resume after invalid optional evidence is removed");
        Sql(path,"CREATE TRIGGER reject_metadata_state BEFORE INSERT ON metadata_state WHEN NEW.name='rejected-state' OR NEW.name='cooldown:rejected' BEGIN SELECT RAISE(FAIL,'fixture constraint'); END");
        check(!history.SetMetadataValue("rejected-state",1)&&!history.SetMetadataCooldown(now.AddHours(1),"rejected")&&history.WriteFailure is null,
            "optional provider-state and cooldown constraints return failure without stopping measurements");
        history.Add(target,new(now,ProbeStatus.Success,3,3,"after optional state failure"));
        check((long)Sql(path,"SELECT COUNT(*) FROM sample")! == 3,"sampling continues after rejected optional provider state");
    }
    private static async Task DiskFailure(string path,Action<bool,string> check)
    {
        await using var history=new History(path);history.Initialize();
        int reported=0;history.WriteFailed+=_=>Interlocked.Increment(ref reported);
        var optional=typeof(History).GetMethod("TryWriteOptional",BindingFlags.NonPublic|BindingFlags.Instance)!;
        bool failed=false;
        try{optional.Invoke(history,[new Action<SqliteConnection>(_=>throw new SqliteException("fixture disk I/O failure",10))]);}
        catch(TargetInvocationException error) when(error.InnerException is SqliteException {SqliteErrorCode:10}){failed=true;}
        // Wait for the writer's diagnostic delivery as well as the request completion.
        await history.DisposeAsync();
        check(failed&&history.WriteFailure is SqliteException {SqliteErrorCode:10}&&reported==1,
            "real SQLite I/O error classes remain visible and stop the writer even for optional writes");
    }
}
