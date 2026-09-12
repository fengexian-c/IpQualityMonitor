using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TcpLatencyMonitor.Core;
using Windows.UI;

namespace TcpLatencyMonitor.App.Controls;

public sealed record ChartSeries(string Name,Timeline Timeline,Color Color);
public sealed partial class MultiLatencyChart : PlotCanvas
{
    public static readonly Color[] Palette={Color.FromArgb(255,24,157,153),Color.FromArgb(255,79,115,215),Color.FromArgb(255,203,118,43),Color.FromArgb(255,166,91,180)};
    private IReadOnlyList<ChartSeries> _series=Array.Empty<ChartSeries>();
    private DateTimeOffset? _selectionFrom,_selectionUntil;
    private readonly PlotPath _axes=new(Muted,1,opacity:.2);
    private readonly List<(PlotPath Line,PlotPath Points,PlotPath Failures)> _paths=new();
    public bool Mini {get;set;}
    public double ScaleMaximum {get;private set;}
    public IReadOnlyList<ChartSeries> Series=>_series;
    public MultiLatencyChart(){Children.Add(_axes.Shape);}
    public void SetSeries(IReadOnlyList<ChartSeries> series){_series=series;InvalidatePlot();}
    public void SelectInterval(DateTimeOffset from,DateTimeOffset until){_selectionFrom=from;_selectionUntil=until;InvalidatePlot();}
    protected override void Draw()
    {
        double width=ActualWidth,height=ActualHeight;if(width<20||height<20)return;
        BeginLabels();_axes.Begin();
        while(_paths.Count>_series.Count){var p=_paths[^1];Children.Remove(p.Line.Shape);Children.Remove(p.Points.Shape);Children.Remove(p.Failures.Shape);_paths.RemoveAt(_paths.Count-1);}
        while(_paths.Count<_series.Count){var color=_series[_paths.Count].Color;var p=(new PlotPath(color,Mini?1.4:2),new PlotPath(color,fill:true),new PlotPath(color,fill:true));_paths.Add(p);Children.Add(p.Item1.Shape);Children.Add(p.Item2.Shape);Children.Add(p.Item3.Shape);}
        if(_series.Count==0){Selection(null,null);if(!Mini)Label("选择已启用相同协议的目标进行对比",0,0,12);_axes.End();EndLabels();return;}
        From=_series[0].Timeline.From;Until=_series[0].Timeline.Until;
        PlotLeft=Mini?2:45;PlotTop=8;PlotWidth=Math.Max(1,width-PlotLeft-8);PlotHeight=Math.Max(1,height-PlotTop-(Mini?8:30));
        ScaleMaximum=Math.Max(10,Math.Ceiling(_series.SelectMany(s=>s.Timeline.Points).Select(p=>p.Average??0).DefaultIfEmpty().Max()/10)*10);
        double Y(double value)=>PlotTop+PlotHeight*(1-value/ScaleMaximum);
        if(!Mini)
        {
            for(int i=0;i<3;i++){double y=PlotTop+i*PlotHeight/2;_axes.Move(PlotLeft,y);_axes.Line(width-5,y);Label((ScaleMaximum*(2-i)/2).ToString("0"),0,y-7);}
            int count=_series[0].Timeline.Days==7?7:4;
            for(int i=0;i<=count;i++){var time=From.AddTicks((Until-From).Ticks*i/count);Label(time.ToLocalTime().ToString(count==7?"MM-dd":"HH:mm"),Math.Clamp(X(time)-15,0,width-32),height-16,9);}
        }
        Selection(Mini?null:_selectionFrom,Mini?null:_selectionUntil);bool any=false;
        for(int s=0;s<_series.Count;s++)
        {
            var series=_series[s];var paths=_paths[s];paths.Line.Begin();paths.Points.Begin();paths.Failures.Begin();
            paths.Line.SetColor(series.Color);paths.Points.SetColor(series.Color);paths.Failures.SetColor(series.Color);
            bool connected=false;
            var points=series.Timeline.Points;
            for(int i=0;i<points.Count;i++)
            {
                var bucket=points[i];
                double x=X(bucket.Start);
                if(bucket.Average is double value)
                {
                    any=true;if(!connected)paths.Line.Move(x,Y(value));else paths.Line.Line(x,Y(value));connected=true;
                    // The mini line still contains every average. Only isolated points
                    // need a marker; hundreds of overlapping dots add no information.
                    if(!Mini||((i==0||!points[i-1].Average.HasValue)&&(i==points.Count-1||!points[i+1].Average.HasValue)))paths.Points.Dot(x,Y(value),Mini?1:2);
                }
                else connected=false;
                if(bucket.Attempts>bucket.Successes){any=true;paths.Failures.Dot(x,PlotTop+PlotHeight+3.5,1.5);}
            }
            paths.Line.End();paths.Points.End();paths.Failures.End();
        }
        if(!any)Label("暂无采样",PlotLeft+Math.Max(0,PlotWidth/2-24),PlotTop+PlotHeight/2-8,Mini?10:12);
        _axes.End();EndLabels();
    }
    protected override int HitIndex(double x)=>Mini||_series.Count==0?-1:BucketAt(x,_series[0].Timeline.Points);
    internal override string TipAt(int index)
    {
        if(_series.Count==0||index<0||index>=_series[0].Timeline.Points.Count)return "";
        var timeline=_series[0].Timeline;var bucket=timeline.Points[index];var end=bucket.Start.AddMinutes(timeline.StepMinutes);if(end>timeline.Until)end=timeline.Until;
        string text=$"{bucket.Start.ToLocalTime():MM-dd HH:mm}—{end.ToLocalTime():HH:mm}";
        foreach(var series in _series)
        {if(index>=series.Timeline.Points.Count)continue;var point=series.Timeline.Points[index];text+=$"\n{series.Name}：{(point.Average is double n?$"{n:0.#} ms":"—")} · 成功 {point.Successes}/{point.Attempts} · 本地错误 {point.LocalErrors}";}
        return text;
    }
}
