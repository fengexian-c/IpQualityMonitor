using TcpLatencyMonitor.Core;

public static class RouteHistoryChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        var time=new DateTimeOffset(2026,9,10,10,0,0,TimeSpan.Zero);
        var target=Target.Parse("203.0.113.50",443,ProbeProtocol.Icmp,1500);
        RouteRun Run(string id,int minute,string? changed="198.51.100.8",int missing=0,string context="test")
        {
            string[] ips=["10.0.0.1","192.0.2.2","192.0.2.3",changed??"","192.0.2.5",target.Address];
            var probes=ips.SelectMany((ip,i)=>Enumerable.Range(1,(i+1==missing||ip=="")?10:3).Select(q=>
                new HopProbe(i+1,q,i+1==missing||ip==""?null:ip,i+1==missing||ip==""?11010:i==5?0:11013,i+1==missing||ip==""?null:10+i)
                {SentAt=time.AddMinutes(minute).AddSeconds(i),IsSupplemental=q>3})).ToArray();
            return new(id,target.Key,target.Address,context,time.AddMinutes(minute),time.AddMinutes(minute).AddSeconds(9),"fixture","fixture",true,1500,32,probes){ProbeOptions=new(),ProbeExecutionId=id};
        }
        RouteHistoryIndex Build(params RouteRun[] runs)=>RouteHistoryIndex.Build(runs.Select(RouteObservation.From));
        var a=Run("a",0);var b=Run("b",5,"198.51.100.9");var u=Run("u",8,null);
        var index=Build(a,b,u);var am=index.Memberships[a.Id];var bm=index.Memberships[b.Id];var um=index.Memberships[u.Id];
        check(index.Patterns.Count==2&&am.PatternId!=bm.PatternId,"route history: conflicting TTLs remain separate patterns");
        check(um.PatternId==bm.PatternId&&um.Kind==RouteMembershipKind.Temporal&&um.EvidenceId==b.Id,"route history: ambiguous timeout follows nearest real path evidence");
        var reference=index.Reference(um,4)!;
        check(reference.Source.Id==b.Id&&reference.Temporal&&!reference.Later,"route history: temporal fill cites the actual donor");
        check(um.Observation.Hops[4] is {Replies:0,Attempts:10,Timeouts:10}&&um.Observation.Hops[4].Addresses.Count==0,"route history: reference leaves raw timeout counts and RTT untouched");
        check(index.Reference(um,3) is null&&index.Reference(um,25) is null,"route history: never fill responsive or unprobed TTLs");
        check(index.Patterns.Sum(p=>index.Memberships.Values.Count(m=>m.PatternId==p.Id))==3,"route history: ambiguous observations count in exactly one column");
        var later=Run("later",9);var revised=Build(a,b,u,later);var revisedU=revised.Memberships[u.Id];
        check(revisedU.EvidenceId==later.Id&&revisedU.Revised&&revisedU.EarlierPatternId==revised.Memberships[b.Id].PatternId,"route history: later nearest evidence revises display with earlier-evidence provenance");
        check(revised.Reference(revisedU,4)!.Later&&revised.Reference(revisedU,4,false) is null,"route history: causal references never use a future donor or cross B");
        var veryLate=Run("stale",24*60+20,null);var old=Build(a,b,u,veryLate);
        check(old.Memberships[veryLate.Id].Kind==RouteMembershipKind.Unknown,"route history: repeated ambiguity never renews expired evidence");
        var partialA=Run("pa",0,missing:3);var partialB=Run("pb",10,missing:5);var complementary=Build(partialA,partialB);
        check(complementary.Patterns.Count==1,"route history: compatible complementary observations share a pattern");
        check(complementary.Reference(complementary.Memberships[partialA.Id],3)?.Source.Id==partialB.Id,"route history: historical gaps can cite later real observations");
        check(complementary.Reference(complementary.Memberships[partialB.Id],5)?.Source.Id==partialA.Id,"route history: latest gaps can cite older real observations");
        var afterReturn=Run("return",20,missing:5);var returning=Build(a,b,afterReturn);
        check(returning.Memberships[afterReturn.Id].PatternId==returning.Memberships[a.Id].PatternId&&returning.Reference(returning.Memberships[afterReturn.Id],5) is null,"route history: A B A keeps a shared column without filling across B");
        var errors=u with{Id="errors",ProbeExecutionId="errors",Probes=u.Probes.Select(p=>p.Ttl==4?p with{Status=11050}:p).ToArray()};
        var errorIndex=Build(a,b,errors);
        check(errorIndex.Reference(errorIndex.Memberships[errors.Id],4) is null,"route history: errors are not fillable timeouts");
        check(errorIndex.Memberships[errors.Id].Kind==RouteMembershipKind.Unknown,"route history R6: a failed discriminator is not a timeout assignment");
        var unprobed=errors with{Id="unprobed",ProbeExecutionId="unprobed",Probes=errors.Probes.Where(p=>p.Ttl!=4).ToArray()};
        check(Build(a,b,unprobed).Memberships[unprobed.Id].Kind==RouteMembershipKind.Unknown,"route history R6: unprobed discriminator cannot be inferred by time");
        RouteRun Two(string id,int minute,string? four,string? five)=>Run(id,minute,four) with{Probes=Run(id,minute,four).Probes.Select(p=>p.Ttl==5?p with{Address=five,Status=five is null?11010:11013,RttMs=five is null?null:15}:p).ToArray()};
        var wa=Two("wA",0,"198.51.100.8",null);var wu=Two("wU",20,null,"198.51.100.50");
        var wb=Two("wB",21,"198.51.100.9",null);var wc=Two("wC",22,null,"198.51.100.51");
        var witnessed=Build(wa,wu,wb,wc);
        check(witnessed.Patterns.Count==2&&new[]{wu,wc}.All(r=>witnessed.Memberships[r.Id] is {Kind:RouteMembershipKind.Temporal} m&&m.PatternId==witnessed.Memberships[wb.Id].PatternId),"route history R1: A/U/B/C partial contributions cannot self-confirm a branch");
        check(witnessed.Reference(witnessed.Memberships[wb.Id],5) is null,"route history R1: inferred contributors cannot become donors");
        var bz=Two("wBZ",23,"198.51.100.9","198.51.100.50");var by=Two("wBY",24,"198.51.100.9","198.51.100.51");
        var split=Build(wa,wu,wb,wc,bz,by);
        check(split.Patterns.Count==3&&split.Memberships[wb.Id].Kind is RouteMembershipKind.Temporal or RouteMembershipKind.Unknown&&split.Memberships[bz.Id].PatternId!=split.Memberships[by.Id].PatternId,"route history R1: only joint b+z / b+y witnesses refine B and retract its earlier certainty");
        var sparseFirst=Build(Two("first",-1,null,"192.0.2.5"),a,b);
        check(sparseFirst.Patterns.Count==2&&sparseFirst.Memberships["first"].Kind==RouteMembershipKind.Temporal,"route history R1: sparse-first input cannot conceal later witnessed conflicts");
        var privateContext=Run("other-network",10,context:"other");
        check(Build(a,privateContext).Patterns.Count==2,"route history: different local network contexts never merge");
        var unknownContext=Run("unknown-1",0,context:"unknown");
        check(Build(unknownContext,unknownContext with{Id="unknown-2",ProbeExecutionId="unknown-2"}).Patterns.Count==2,"route history: unresolved contexts are not a wildcard");
        var shortRun=a with{Id="short",ProbeExecutionId="short",Probes=a.Probes.Where(p=>p.Ttl<=3).Select(p=>p.Ttl==3?p with{Address=target.Address,Status=0}:p).ToArray()};
        check(Build(a,shortRun).Patterns.Count==2,"route history: actual destination TTL matters, not MaxHops");
        var onlyTarget=a with{Id="endpoint-only",ProbeExecutionId="endpoint-only",Probes=a.Probes.Where(p=>p.Ttl==6).ToArray()};
        check(Build(a,b,onlyTarget).Memberships[onlyTarget.Id].Kind==RouteMembershipKind.Unknown,"route history: endpoint alone cannot establish a path");
        var multi=a with{Id="multi",ProbeExecutionId="multi",Started=time.AddMinutes(2),Finished=time.AddMinutes(2).AddSeconds(9),Probes=a.Probes.Append(new HopProbe(4,4,"198.51.100.9",11013,14)).ToArray()};
        var multipath=Build(a,multi,b);
        check(multipath.Patterns.Count==2&&multipath.Memberships[multi.Id].Kind==RouteMembershipKind.Multiple,"route history: overlapping IP sets never bridge conflicting variants");
        check(Build(a,a with{Id="copy",TargetKey="other-protocol"}).Observations.Count==1,"route history: physical execution copies are counted once");
        check(Build(a,a with{Id="legacy-copy",ProbeExecutionId=null}).Observations.Count==2,"route history: legacy identical snapshots are not presumed duplicates");
        var reversed=Build(later,u,b,a);
        check(reversed.Revision==revised.Revision&&reversed.Memberships[u.Id].PatternId==revisedU.PatternId&&reversed.Memberships[u.Id].EvidenceId==revisedU.EvidenceId,"route history: input ordering cannot change classification");
        check(revised.Window(time.AddMinutes(7),time.AddMinutes(9)).Single().PatternId==revisedU.PatternId,"route history: time filters never redefine candidate patterns");
        var equalTime=Build(a,Run("tie-b",4,"198.51.100.9"),Run("equal",2,null));
        check(equalTime.Memberships["equal"].EvidenceId==a.Id,"route history: equal time distances prefer earlier real evidence");
        var simultaneous=Build(Run("z-time",0),Run("a-time",0),Run("b-time",4,"198.51.100.9"),Run("u-time",2,null));
        check(simultaneous.Memberships["u-time"].EvidenceId=="a-time","route history: simultaneous past witnesses use the smallest stable ID");
        var paused=RouteHistoryIndex.Build(new[]{partialA,partialB}.Select(RouteObservation.From),boundaries:new[]{time.AddMinutes(5)});
        check(paused.Reference(paused.Memberships[partialB.Id],5) is null,"route history: stop/resume boundary prevents historical filling");
        var richer=Build(partialA,partialB,Run("richer",20));
        check(richer.Patterns.Single().Id==complementary.Patterns.Single().Id,"route history: compatible richer observations retain a stable pattern identity");
        var many=Enumerable.Range(0,1200).Select(i=>Run("bulk-"+i,i-1200,i%10==0?null:i%2==0?"198.51.100.8":"198.51.100.9")).ToArray();
        var clock=System.Diagnostics.Stopwatch.StartNew();var bulk=Build(many);
        check(bulk.Observations.Count==1200&&bulk.Patterns.Count==2&&bulk.Window(time.AddDays(-1),time).Count==1200,"route history: a full time window includes more than 200 records");
        Console.WriteLine($"Route history 1200-record projection: {clock.ElapsedMilliseconds} ms");
        using(var cancel=new CancellationTokenSource()){cancel.Cancel();bool stopped=false;try{RouteHistoryIndex.Build(new[]{RouteObservation.From(a)},cancel.Token);}catch(OperationCanceledException){stopped=true;}check(stopped,"route history: cancelled background analysis stops promptly");}
        string folder=Path.Combine(Path.GetTempPath(),"route-history-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        await using var history=new History(Path.Combine(folder,"history.db"));history.Initialize();
        foreach(var run in new[]{a,b,u,later})history.SaveRoute(run);
        var loaded=history.LoadRouteHistory(target);
        check(loaded.Observations.Count==4&&loaded.Memberships[u.Id].Kind==RouteMembershipKind.Temporal,"route history: compact SQLite projection preserves classifications");
        check(history.LoadRoute(target,u.Id)!.Probes.Count(p=>p.Ttl==4&&p.Address is null)==10,"route history: querying projection never rewrites route JSON");
        check(history.LoadRoute(target,a.Id)!.ProbeExecutionId==a.ProbeExecutionId,"route history: Native JSON context includes shared execution identity");
        check(history.RouteHistoryStamp(target).StartsWith("4:"),"route history: retained data stamp invalidates bounded UI cache");
        var stampBeforeBoundary=history.RouteHistoryStamp(target);
        history.AddEvent(new("pause",target.Key,time.AddMinutes(7),"Stopped","fixture"));
        check(loaded.Revision!=history.LoadRouteHistory(target).Revision&&history.RouteHistoryStamp(target)!=stampBeforeBoundary,"route history R9: boundary-only writes invalidate the source version");
        check(history.LoadRouteHistory(target).Reference(history.LoadRouteHistory(target).Memberships[u.Id],4,false) is null,"route history: SQLite monitoring boundaries are honored");
        await RouteAnalysisChecks.Run(check);
    }
}
