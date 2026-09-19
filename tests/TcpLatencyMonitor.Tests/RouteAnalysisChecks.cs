using Microsoft.Data.Sqlite;
using System.Diagnostics;
using TcpLatencyMonitor.Core;

public static class RouteAnalysisChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        var time=DateTimeOffset.UtcNow.AddHours(-3);var target=Target.Parse("203.0.113.50",443,ProbeProtocol.Icmp,1500);
        RouteRun Run(string id,int minute,string? four="198.51.100.8",string? five="192.0.2.5")
        {
            string?[] ips=["10.0.0.1","192.0.2.2","192.0.2.3",four,five,target.Address];
            return new(id,target.Key,target.Address,"analysis-fixture",time.AddMinutes(minute),time.AddMinutes(minute).AddSeconds(9),"fixture","fixture",true,1500,32,
                ips.SelectMany((ip,i)=>Enumerable.Range(1,3).Select(q=>new HopProbe(i+1,q,ip,ip is null?11010:i==5?0:11013,ip is null?null:10+i){SentAt=time.AddMinutes(minute).AddSeconds(i),IsSupplemental=q==3})).ToArray()){ProbeOptions=new(),ProbeExecutionId=id};
        }
        RouteRun Multi(string id,params string[] ips)=>Run(id,1) with{Probes=Run(id,1).Probes.Where(p=>p.Ttl!=4).Concat(ips.Select((ip,i)=>new HopProbe(4,i+1,ip,11013,14))).ToArray()};
        var multiRuns=new[]{Multi("m1","198.51.100.1","198.51.100.2"),Multi("m2","198.51.100.2","198.51.100.3"),Multi("m3","198.51.100.3","198.51.100.4")};
        var multi=RouteHistoryIndex.Build(multiRuns.Select(RouteObservation.From));
        check(multi.Patterns.Count==3&&multi.Memberships.Values.Select(m=>m.PatternId).Distinct().Count()==3,"route history: overlapping multi-response chains retain separate observed modes");
        var joined=Multi("combined","198.51.100.1","198.51.100.2","198.51.100.3","198.51.100.4");
        var joinedIndex=RouteHistoryIndex.Build(multiRuns.Append(joined).Select(RouteObservation.From));
        check(joinedIndex.Memberships[joined.Id].Kind==RouteMembershipKind.Multiple&&joinedIndex.Patterns.Count==3,"route history: a multi-set superset cannot bridge existing modes");
        var partial=Run("phase",0);var phase=RouteObservation.From(partial);
        check(phase.Hops[4].Addresses.Single().Supplemental,"route history: normalization retains supplemental provenance");

        string folder=Path.Combine(Path.GetTempPath(),"route-analysis-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"history.db"),analysisPath=Path.Combine(folder,"route-analysis.db");
        await using var history=new History(path);history.Initialize();history.SaveRoute(Run("legacy",-1,null));
        RouteFirstDecision first;long parsed;
        await using(var service=new RouteAnalysisService(history,analysisPath))
        {
            var legacy=await service.GetAsync(target);
            check(!legacy.FirstDecisions.ContainsKey("legacy"),"route history R7: pre-upgrade observations have no fabricated first decision");
            var inputs=new[]{Run("sparse",0,null),Run("A",1),Run("B",2,"198.51.100.9"),Run("U",3,null),Run("C",4,"198.51.100.10")};
            history.SaveRoute(inputs[0]);var initial=await service.GetAsync(target);first=initial.FirstDecisions["sparse"];
            check(first.Origin=="在线首次判断"&&first.Kind==RouteMembershipKind.Exact&&first.ComputedAt>first.EvidenceCutoff,"route history R7: first computation is stored with its real time and causal cutoff");
            parsed=service.RawJsonParsed;
            for(int i=1;i<inputs.Length;i++)
            {
                history.SaveRoute(inputs[i]);var actual=await service.GetAsync(target);var expected=history.LoadRouteHistory(target);
                check(actual.Memberships.All(p=>expected.Memberships[p.Key].PatternId==p.Value.PatternId&&expected.Memberships[p.Key].Kind==p.Value.Kind&&expected.Memberships[p.Key].EvidenceId==p.Value.EvidenceId),"route history: cached append prefix "+i+" equals full rebuild");
            }
            var current=await service.GetAsync(target);
            check(service.RawJsonParsed-parsed==inputs.Length-1,"route history R8: ordinary appends never reparse old raw JSON");
            check(current.FirstDecisions["sparse"]==first&&first.Changes(current.Memberships["sparse"]).Contains("状态变化"),"route history R7: later branches show correction without overwriting the first judgement");
            long incremental=service.IncrementalAppends,rebuilt=service.Rebuilds;
            for(int i=0;i<5;i++)
            {
                history.SaveRoute(Run("repeat-"+i,10+i,i%2==0?"198.51.100.8":"198.51.100.9"));current=await service.GetAsync(target);var oracle=history.LoadRouteHistory(target);
                check(current.Revision==oracle.Revision&&current.Memberships.All(p=>p.Value.PatternId==oracle.Memberships[p.Key].PatternId&&p.Value.Kind==oracle.Memberships[p.Key].Kind&&p.Value.EvidenceId==oracle.Memberships[p.Key].EvidenceId),"route history: repeated append re-evaluates ambiguous dependencies "+i);
            }
            check(service.IncrementalAppends-incremental==5&&service.Rebuilds==rebuilt,"route history R8: repeated signatures reuse the branch catalogue without full rebuild");
            long unchanged=service.RawJsonParsed;var same=await service.GetAsync(target);
            check(ReferenceEquals(current,same)&&service.RawJsonParsed==unchanged,"route history: unchanged source reuses the complete index");
            history.AddEvent(new("stop",target.Key,time.AddMinutes(2.5),"Stopped","fixture"));var boundary=await service.GetAsync(target);
            check(boundary.Revision!=same.Revision&&service.RawJsonParsed==unchanged,"route history R9: boundary-only change rebuilds without parsing route JSON");
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path}.ToString()))
            {
                db.Open();using var cmd=db.CreateCommand();cmd.CommandText="DELETE FROM route_run WHERE id='B'";cmd.ExecuteNonQuery();
            }
            var deleted=await service.GetAsync(target);var full=history.LoadRouteHistory(target);
            check(deleted.Revision==full.Revision&&!deleted.Memberships.ContainsKey("B"),"route history: retained-source deletion invalidates cached membership");
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path}.ToString()))
            {
                db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE route_run SET json=replace(json,'198.51.100.10','198.51.100.11') WHERE id='C'";cmd.ExecuteNonQuery();
            }
            var replaced=await service.GetAsync(target);
            check(replaced.Memberships["C"].Observation.Hops[4].Addresses.Single().Address=="198.51.100.11"&&service.RawJsonParsed==unchanged+1,"route history: same-size raw record replacement is detected by mutation stamp");
        }
        history.SaveRoute(Run("offline",5));
        await using(var restarted=new RouteAnalysisService(history,analysisPath))
        {
            var reload=await restarted.GetAsync(target);
            check(reload.FirstDecisions["sparse"]==first,"route history R7: restart retains immutable first judgement");
            check(reload.FirstDecisions["offline"].Origin=="重启补算"&&restarted.RawJsonParsed==1,"route history: restart reconciles only new raw data and labels catch-up honestly");
        }
        string invalidPath=Path.Combine(folder,"blocked.db");Directory.CreateDirectory(invalidPath);
        await using(var broken=new RouteAnalysisService(history,invalidPath))
        {
            bool failed=false;try{await broken.GetAsync(target);}catch(SqliteException){failed=true;}
            history.SaveRoute(Run("still-saving",6));
            check(failed&&history.WriteFailure is null&&history.LoadRoute(target,"still-saving") is not null,"route history: derived DB failure cannot stop raw sampling writes");
        }
        foreach(int count in new[]{400,1200,2400})
        {
            var runs=Enumerable.Range(0,count).Select(i=>Run("distinct-"+i,i,$"198.{(i>>16)&255}.{(i>>8)&255}.{i&255}")).ToArray();
            long before=GC.GetTotalAllocatedBytes(true);var watch=Stopwatch.StartNew();var distinct=RouteHistoryIndex.Build(runs.Select(RouteObservation.From));watch.Stop();
            double mib=(GC.GetTotalAllocatedBytes(true)-before)/1048576d;
            Console.WriteLine($"DISTINCT: {count} records, {watch.ElapsedMilliseconds} ms, {mib:F2} MiB cumulative allocation");
            check(distinct.Patterns.Count==count&&mib<128,"route history R8: distinct-path allocation stays below 128 MiB at "+count);
        }
        await using(var raceHistory=new History(Path.Combine(folder,"race-history.db")))
        {
            raceHistory.Initialize();using var release=new ManualResetEventSlim();var committed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            raceHistory.RouteSourceCommitted+=(_,id)=>{if(id=="race"){committed.TrySetResult();release.Wait(TimeSpan.FromSeconds(5));}};
            await using var service=new RouteAnalysisService(raceHistory,Path.Combine(folder,"race-analysis.db"));await service.GetAsync(target);await Task.Delay(10);
            var saving=Task.Run(()=>raceHistory.SaveRoute(Run("race",20)));
            try
            {
                await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));var index=await service.GetAsync(target);
                check(index.FirstDecisions["race"].Origin=="在线首次判断","route history: a committed row read before its callback still records an online first judgement");
            }
            finally{release.Set();await saving;}
        }
    }
}
