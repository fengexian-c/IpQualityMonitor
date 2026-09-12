using System.Text.Json;

namespace TcpLatencyMonitor.Core;

public sealed record TargetOverview(string ProfileId,Target? Target,Timeline? Timeline,double? P95Hour,Sample? Latest,RouteRun? Route,RouteAnnotation? Annotation);
public sealed record MultiOverview(DateTimeOffset AsOf,int Days,ProbeProtocol Protocol,IReadOnlyList<TargetOverview> Targets);

public sealed partial class History
{
    public MultiOverview LoadOverview(IReadOnlyList<TargetProfile> profiles,ProbeProtocol protocol,DateTimeOffset now,int days,CancellationToken token=default)
    {
        using var db=Open();using var tx=db.BeginTransaction(deferred:true);
        var result=new List<TargetOverview>();
        var measurements=new Dictionary<string,(Timeline Timeline,double? P95,Sample? Latest)>();
        foreach(var profile in profiles)
        {
            token.ThrowIfCancellationRequested();var target=profile.ForProtocol(protocol);
            Timeline? timeline=null;double? p95=null;Sample? latest=null;
            if(target is not null)
            {
                if(!measurements.TryGetValue(target.Key,out var measurement))
                {
                    timeline=LoadTimeline(db,target,now,days);
                    var values=new List<double>();
                    using(var cmd=Range(db,target,now.AddHours(-1),now.AddMilliseconds(1),"SELECT latency FROM sample WHERE "+Filter+" AND status=0 ORDER BY latency"))
                    using(var reader=cmd.ExecuteReader())while(reader.Read())values.Add(reader.GetDouble(0));
                    p95=Percentile95(values);
                    using(var cmd=Range(db,target,now.AddDays(-31),now.AddMilliseconds(1),"SELECT time_ms,status,latency,elapsed,detail,context,native_status FROM sample WHERE "+Filter+" ORDER BY time_ms DESC,id DESC LIMIT 1"))
                    using(var reader=cmd.ExecuteReader())if(reader.Read())latest=ReadSample(reader);
                    measurement=(timeline,p95,latest);measurements.Add(target.Key,measurement);
                }
                (timeline,p95,latest)=measurement;
            }
            RouteRun? route=null;RouteAnnotation? annotation=null;
            using(var cmd=db.CreateCommand())
            {
                cmd.CommandText="SELECT json FROM route_run WHERE target=$target ORDER BY time_ms DESC LIMIT 1";
                cmd.Parameters.AddWithValue("$target",profile.Primary.Key);
                if(cmd.ExecuteScalar() is string json)route=JsonSerializer.Deserialize(json,DataJson.Default.RouteRun);
            }
            if(route is not null)
            {
                using var cmd=db.CreateCommand();cmd.CommandText="SELECT json FROM route_annotation WHERE route_id=$id ORDER BY time_ms DESC,rowid DESC LIMIT 1";cmd.Parameters.AddWithValue("$id",route.Id);
                if(cmd.ExecuteScalar() is string json)annotation=JsonSerializer.Deserialize(json,DataJson.Default.RouteAnnotation);
            }
            result.Add(new(profile.Id,target,timeline,p95,latest,route,annotation));
        }
        return new(now,days,protocol,result);
    }
}
