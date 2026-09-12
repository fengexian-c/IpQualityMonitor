using TcpLatencyMonitor.Core;

internal static class RouteTableChecks
{
    public static void Run(Action<bool,string> check)
    {
        var now=DateTimeOffset.UtcNow;const string a="59.43.1.1",b="59.43.1.2",target="8.8.4.4";
        NodeMetadata M(string ip,string city="上海",long? asn=null)=>new(ip,true,asn,"fixture","fixture","中国",city,city,now,now.AddDays(7),"");
        RouteRun R(params HopProbe[] probes)=>new(Guid.NewGuid().ToString("N"),"fixture",target,"fixture",now,now,"fixture","fixture",false,500,32,probes);
        RouteTableModel Table(RouteRun route,bool all=false,params NodeMetadata[] data)=>RouteTable.Build(route,RouteClassifier.Classify(route,data.ToDictionary(m=>m.Address),now,"fixture"),all);
        var route=R(new(1,1,"10.0.0.1",11013,1),new(2,1,a,11013,10),new(2,2,a,11013,20),new(2,3,null,11010,null),
            new(3,1,b,11013,20),new(3,2,b,11013,30),new(3,3,b,11013,40),new(4,1,target,0,0));
        var model=Table(route,false,M(a),M(b),M(target));
        check(model.HiddenNonPublic==1&&model.Rows.Count==2&&model.Rows[0].Hops=="2–3"&&model.MergedNodes==1,"overview hides private hops and merges only adjacent same-city same-network nodes");
        check(model.Rows[0].Nodes[0].AverageRtt==15&&model.Rows[0].Nodes[1].AverageRtt==30&&model.Rows[0].Rtt=="15～30","merged RTT spans per-hop means and timeouts do not dilute averages");
        check(model.Rows[0].Addresses.Length==2&&model.Rows[0].Nodes[0].ReplyCount==2&&model.Rows[0].Nodes[0].Attempts==3,"merged rows retain IPs, sample counts and individual probe evidence");
        check(model.Rows[^1].Target&&model.Rows[^1].Rtt=="<1","endpoint stays separate and zero-millisecond native timing is displayed as less than one");
        var all=Table(route,true,M(a),M(b),M(target));
        check(all.Rows.Count==3&&all.Rows[0].Nodes[0].Network=="私网地址"&&all.HiddenNonPublic==0,"show-all restores private rows with accurate private-address wording");
        var gap=R(new(1,1,a,11013,10),new(2,1,null,11010,null),new(3,1,b,11013,30));
        var hidden=Table(gap,false,M(a),M(b));
        check(hidden.Rows.Count==2&&hidden.HiddenNoReply==1&&!hidden.Rows.Any(r=>r.Merged),"filtering an unresponsive hop cannot create artificial adjacency");
        check(Table(gap,true,M(a),M(b)).Rows[1].Rtt=="—","unresponsive rows have no fabricated zero RTT");
        var privateGap=gap with{Probes=new HopProbe[]{new(1,1,a,11013,10),new(2,1,"10.1.1.1",11013,1),new(3,1,b,11013,30)}};
        check(Table(privateGap,false,M(a),M(b)).Rows.Count==2,"filtering a private hop cannot merge through it");
        var pair=R(new(1,1,a,11013,10),new(2,1,b,11013,30));
        check(Table(pair,false,M(a),M(b,"北京")).Rows.Count==2,"a city change splits otherwise matching backbone rows");
        var differentNetwork=R(new(1,1,a,11013,10),new(2,1,"1.1.1.1",11013,30));
        check(Table(differentNetwork,false,M(a),M("1.1.1.1","上海",4837)).Rows.Count==2,"a network change splits same-city rows");
        check(Table(pair).Rows.Count==2,"unknown city is not evidence of a shared city");
        check(Table(pair,false,M(a) with{City=""},M(b) with{City=""}).Rows.Count==2,"matching province-only geography is not collapsed as matching cities");
        check(Table(pair,false,M(a),M(b) with{Country="另一国家"}).Rows.Count==2,"same-named cities in different countries do not merge");
        var multi=R(new(1,1,a,11013,10),new(1,2,b,11013,20),new(2,1,"59.43.1.3",11013,30));
        check(Table(multi,false,M(a),M(b),M("59.43.1.3")).Rows.Count==3,"multiple IPs at one TTL stay on separate rows and block cross-TTL merging");
        var mixed=multi with{Probes=new HopProbe[]{new(1,1,a,11013,10),new(1,2,"10.0.0.1",11013,20),new(2,1,b,11013,30)}};
        check(Table(mixed,false,M(a),M(b)).Rows.Count==2,"a hidden private alternative still preserves multipath ambiguity");
        var duplicate=R(new(1,1,a,11013,10),new(2,1,a,11013,30));
        check(Table(duplicate,false,M(a)).Rows.Single().Addresses.Length==1,"one IP repeated across merged hops is shown once while preserving hop range");
        var end=duplicate with{Address=a,Probes=new HopProbe[]{new(1,1,a,11013,10),new(2,1,a,0,30)}};
        check(Table(end,false,M(a)).Rows.Count==2&&Table(end,false,M(a)).Rows[^1].Target,"target echo reply is never merged with an intermediate TTL-expiry response");
        var localTarget=R(new HopProbe(1,1,"10.0.0.2",0,0)) with{Address="10.0.0.2"};
        check(Table(localTarget).Rows.Single().Target&&Table(localTarget).HiddenNonPublic==0,"private-address target remains visible under the default filter");
        var error=R(new HopProbe(1,1,"1.1.1.1",11003,999));
        check(Table(error).Rows.Single().Nodes[0].State=="error"&&Table(error).Rows[0].Rtt=="—","explicit diagnostic errors remain visible and do not supply successful RTT");
        var invalid=R(new(1,1,a,11013,double.NaN),new(1,2,a,11013,-1),new(1,3,a,11013,12));
        check(Table(invalid,false,M(a)).Rows.Single().Rtt=="12","invalid numeric RTT values cannot corrupt a node mean");
        var partial=pair with{Probes=new HopProbe[]{new(1,1,a,11013,null),new(2,1,b,11013,30)}};
        check(Table(partial,false,M(a),M(b)).Rows.Single().Rtt=="30 *","merged range flags nodes with no valid RTT instead of silently treating them as zero");
        var absent=R(new(1,1,a,11013,10),new(3,1,b,11013,30));
        check(Table(absent,false,M(a),M(b)).Rows[1].Nodes[0].State=="missing","unrecorded TTL is distinct from a probe timeout");
        var empty=Table(R(new HopProbe(1,1,null,11010,null)));
        check(empty.Rows.Count==0&&empty.HiddenNoReply==1,"fully filtered route retains an explanation count");
        var many=R(Enumerable.Range(1,10).Select(i=>new HopProbe(i,1,i==10?target:$"1.1.1.{i}",i==10?0:11013,i)).ToArray());
        var manyTable=Table(many);
        check(manyTable.Rows.Count==10&&manyTable.Rows.Select(r=>r.First).SequenceEqual(Enumerable.Range(1,10))&&manyTable.Rows[^1].Target,"overview retains every filtered row in hop order including the target");
        var annotation=RouteClassifier.Classify(route,new Dictionary<string,NodeMetadata>(),now,"fixture");bool refused=false;
        try{RouteTable.Build(route,annotation with{RouteId="wrong"});}catch(ArgumentException){refused=true;}
        check(refused,"table cannot mix a route with another route's annotation");
    }
}
