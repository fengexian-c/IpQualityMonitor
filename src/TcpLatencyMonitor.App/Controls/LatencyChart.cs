using TcpLatencyMonitor.Core;
using Windows.UI;

namespace TcpLatencyMonitor.App.Controls;

public sealed partial class LatencyChart : PlotCanvas
{
    private IReadOnlyList<Bucket> _buckets=Array.Empty<Bucket>();
    private int _stepMinutes=1;
    private DateTimeOffset _until=DateTimeOffset.UtcNow;
    private DateTimeOffset? _selectedFrom,_selectedUntil;
    private readonly PlotPath _axes=new(Muted,1,opacity:.2),_line=new(MultiLatencyChart.Palette[0],2),_ranges=new(MultiLatencyChart.Palette[0],2,opacity:.35),_points=new(MultiLatencyChart.Palette[0],fill:true),_failures=new(Color.FromArgb(255,221,82,82),fill:true);
    internal int FailureMarkerCount=>_failures.FigureCount;
    internal int SegmentCount=>_line.FigureCount;
    internal int SampleMarkerCount=>_points.FigureCount;
    internal double ScaleMaximum {get;private set;}
    public LatencyChart(){foreach(var path in new[]{_axes,_ranges,_line,_points,_failures})Children.Add(path.Shape);}
    public void SetData(IReadOnlyList<Bucket> buckets,int stepMinutes=1,DateTimeOffset? until=null){_buckets=buckets;_stepMinutes=stepMinutes;_until=until??DateTimeOffset.UtcNow;InvalidatePlot();}
    public void SelectInterval(DateTimeOffset from,DateTimeOffset until){_selectedFrom=from;_selectedUntil=until;InvalidatePlot();}
    public void ClearSelection(){_selectedFrom=_selectedUntil=null;InvalidatePlot();}
    protected override void Draw()
    {
        double w=ActualWidth,h=ActualHeight;if(w<100||h<60)return;
        BeginLabels();foreach(var path in new[]{_axes,_ranges,_line,_points,_failures})path.Begin();
        PlotLeft=46;PlotTop=12;PlotWidth=w-PlotLeft-6;PlotHeight=h-PlotTop-34;From=_buckets.Count>0?_buckets[0].Start:_until;Until=_until;
        bool week=(Until-From).TotalHours>48;int tickHours=week?24:6;var local=From.ToLocalTime();
        var tick=new DateTimeOffset(local.Year,local.Month,local.Day,0,0,0,local.Offset).AddHours((local.Hour/tickHours+1)*tickHours);
        for(;tick<Until;tick=tick.AddHours(tickHours)){double x=X(tick);_axes.Move(x,PlotTop);_axes.Line(x,PlotTop+PlotHeight);Label(tick.ToLocalTime().ToString(week?"MM-dd":"HH:mm"),x-15,h-15,9);}
        Selection(_selectedFrom,_selectedUntil);
        ScaleMaximum=Math.Max(10,Math.Ceiling(_buckets.Select(b=>b.Maximum??b.Average??0).DefaultIfEmpty().Max()/10)*10);
        double Y(double value)=>PlotTop+PlotHeight*(1-value/ScaleMaximum);
        for(int i=0;i<3;i++){double y=PlotTop+i*PlotHeight/2;_axes.Move(PlotLeft,y);_axes.Line(w,y);Label((ScaleMaximum*(2-i)/2).ToString("0"),0,y-7);}
        bool any=false,connected=false;
        foreach(var bucket in _buckets)
        {
            double x=X(bucket.Start);
            if(bucket.Average is double average)
            {
                any=true;if(!connected)_line.Move(x,Y(average));else _line.Line(x,Y(average));connected=true;
                if(bucket.Minimum is double min&&bucket.Maximum is double max){_ranges.Move(x,Y(min));_ranges.Line(x,Y(max));}_points.Dot(x,Y(average),2.5);
            }
            else connected=false;
            if(bucket.Attempts>bucket.Successes){any=true;_failures.Dot(x,PlotTop+PlotHeight+6.5,2.5);}
        }
        if(!any)Label("暂无有效采样",PlotLeft+PlotWidth/2-45,h/2-12,13);
        foreach(var path in new[]{_axes,_ranges,_line,_points,_failures})path.End();EndLabels();
    }
    protected override int HitIndex(double x)=>BucketAt(x,_buckets);
    internal override string TipAt(int index)
    {
        if(index<0||index>=_buckets.Count)return "";var b=_buckets[index];var end=b.Start.AddMinutes(_stepMinutes)>_until?_until:b.Start.AddMinutes(_stepMinutes);
        return $"{b.Start.ToLocalTime():MM-dd HH:mm}—{end.ToLocalTime():MM-dd HH:mm}\n平均 {b.Average?.ToString("F1")??"—"} ms · 最低 {b.Minimum?.ToString("F1")??"—"} · 最高 {b.Maximum?.ToString("F1")??"—"}\n成功 {b.Successes}/{b.Attempts} · 本地错误 {b.LocalErrors}";
    }
}
