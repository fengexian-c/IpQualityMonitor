using Microsoft.Data.Sqlite;

namespace TcpLatencyMonitor.Core;

public sealed record Timeline(DateTimeOffset From,DateTimeOffset Until,int Days,int StepMinutes,
    IReadOnlyList<Bucket> Points,IReadOnlyList<Bucket> Hours,Summary Total);

public sealed partial class History
{
    // Both visualizations consume one read snapshot and the same minute-aligned window.
    public Timeline LoadTimeline(Target target,DateTimeOffset now,int days,TimeZoneInfo? zone=null)
    {
        using var db=Open();using var tx=db.BeginTransaction(deferred:true);
        return LoadTimeline(db,target,now,days,zone);
    }
    private static Timeline LoadTimeline(SqliteConnection db,Target target,DateTimeOffset now,int days,TimeZoneInfo? zone=null)
    {
        if(days is not (1 or 7))throw new ArgumentOutOfRangeException(nameof(days));
        zone??=TimeZoneInfo.Local;
        long minute=now.ToUnixTimeMilliseconds()/60000*60000;
        var from=DateTimeOffset.FromUnixTimeMilliseconds(minute).AddDays(-days);var until=now.AddMilliseconds(1);
        int step=days==1?5:30;
        var local=TimeZoneInfo.ConvertTime(from,zone);
        var hourStart=new DateTimeOffset(local.Year,local.Month,local.Day,local.Hour,0,0,local.Offset).ToUniversalTime();
        var points=Enumerable.Range(0,(int)((until-from).TotalMinutes/step)+1).Select(i=>new Accumulator(from.AddMinutes(i*step))).ToArray();
        var hours=Enumerable.Range(0,(int)Math.Ceiling((until-hourStart).TotalHours)).Select(i=>new Accumulator(hourStart.AddHours(i))).ToArray();
        var total=new Accumulator(from);
        using var cmd=Range(db,target,from,until,"""
            SELECT bucket_ms,attempts,successes,errors,total,low,high FROM minute_summary
              WHERE target=$t AND bucket_ms >= $from AND bucket_ms < ($until/60000)*60000
            UNION ALL SELECT (time_ms/60000)*60000,COUNT(CASE WHEN status<>4 THEN 1 END),COUNT(CASE WHEN status=0 THEN 1 END),COUNT(CASE WHEN status=4 THEN 1 END),
              SUM(CASE WHEN status=0 THEN latency END),MIN(CASE WHEN status=0 THEN latency END),MAX(CASE WHEN status=0 THEN latency END)
              FROM sample WHERE target=$t AND time_ms >= ($until/60000)*60000 AND time_ms < $until GROUP BY time_ms/60000
            """);
        using var r=cmd.ExecuteReader();
        while(r.Read())
        {
            var time=DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0));
            long attempts=r.GetInt64(1),success=r.GetInt64(2),errors=r.GetInt64(3);double sum=NullableDouble(r,4)??0;
            void Add(Accumulator a)=>a.Add(attempts,success,errors,sum,NullableDouble(r,5),NullableDouble(r,6));
            Add(points[(int)((time-from).TotalMinutes/step)]);Add(hours[(int)((time-hourStart).TotalHours)]);Add(total);
        }
        return new(from,until,days,step,points.Select(a=>a.Bucket()).ToArray(),hours.Select(a=>a.Bucket()).ToArray(),
            new(total.Attempts,total.Success,total.Errors,total.Success==0?null:total.Sum/total.Success,total.Min,total.Max));
    }
    private sealed class Accumulator(DateTimeOffset start)
    {
        public long Attempts,Success,Errors;public double Sum;public double? Min,Max;
        public void Add(long attempts,long success,long errors,double sum,double? min,double? max)
        {
            Attempts+=attempts;Success+=success;Errors+=errors;Sum+=sum;
            if(min.HasValue)Min=Min.HasValue?Math.Min(Min.Value,min.Value):min;
            if(max.HasValue)Max=Max.HasValue?Math.Max(Max.Value,max.Value):max;
        }
        public Bucket Bucket()=>new(start,Attempts,Success,Errors,Success==0?null:Sum/Success,Min,Max);
    }
}
