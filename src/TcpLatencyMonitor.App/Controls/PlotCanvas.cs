using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using PlotShape=Microsoft.UI.Xaml.Shapes.Path;

namespace TcpLatencyMonitor.App.Controls;

// Reuse native figures and point collections as well as the visual elements.
internal sealed class PlotPath
{
    public PlotShape Shape {get;}
    private readonly SolidColorBrush _brush;
    private readonly PathGeometry _geometry=new(){FillRule=FillRule.Nonzero};
    private readonly GeometryGroup _dots=new(){FillRule=FillRule.Nonzero};
    private readonly List<EllipseGeometry> _ellipses=new();
    private readonly bool _filled;
    private readonly List<(PathFigure Figure,PointCollection Points)> _figures=new();
    private int _used;
    private PointCollection? _current;
    private int _pointIndex;
    public int FigureCount=>_used;
    public PlotPath(Color color,double thickness=1,bool fill=false,double opacity=1)
    {
        _filled=fill;_brush=new SolidColorBrush(color);
        Shape=new(){Data=fill?_dots:_geometry,Stroke=fill?null:_brush,Fill=fill?_brush:null,StrokeThickness=thickness,Opacity=opacity,IsHitTestVisible=false,StrokeLineJoin=PenLineJoin.Round};
    }
    public void SetColor(Color color){if(_brush.Color!=color)_brush.Color=color;}
    public void Begin(){_used=0;_current=null;_pointIndex=0;}
    private void FinishFigure()
    {if(_current is not null)while(_current.Count>_pointIndex)_current.RemoveAt(_current.Count-1);}
    public void Move(double x,double y,bool closed=false)
    {
        FinishFigure();
        if(_used==_figures.Count)
        {var points=new PointCollection();var segment=new PolyLineSegment{Points=points};var figure=new PathFigure();figure.Segments.Add(segment);_geometry.Figures.Add(figure);_figures.Add((figure,points));}
        var pair=_figures[_used++];var start=new Point(x,y);
        if(pair.Figure.StartPoint!=start)pair.Figure.StartPoint=start;
        if(pair.Figure.IsClosed!=closed)pair.Figure.IsClosed=closed;
        if(pair.Figure.IsFilled!=closed)pair.Figure.IsFilled=closed;
        _current=pair.Points;_pointIndex=0;
    }
    public void Line(double x,double y)
    {
        var point=new Point(x,y);
        if(_pointIndex<_current!.Count){if(_current[_pointIndex]!=point)_current[_pointIndex]=point;}else _current.Add(point);
        _pointIndex++;
    }
    public void Dot(double x,double y,double r)
    {
        if(_used==_ellipses.Count){var dot=new EllipseGeometry{RadiusX=r,RadiusY=r};_ellipses.Add(dot);_dots.Children.Add(dot);}
        var ellipse=_ellipses[_used++];var center=new Point(x,y);
        if(ellipse.Center!=center)ellipse.Center=center;
        if(ellipse.RadiusX!=r)ellipse.RadiusX=ellipse.RadiusY=r;
    }
    public void End()
    {
        FinishFigure();
        while(_ellipses.Count>_used){_ellipses.RemoveAt(_ellipses.Count-1);_dots.Children.RemoveAt(_dots.Children.Count-1);}
        while(!_filled&&_figures.Count>_used){_figures.RemoveAt(_figures.Count-1);_geometry.Figures.RemoveAt(_geometry.Figures.Count-1);}
        var visibility=_used==0?Visibility.Collapsed:Visibility.Visible;if(Shape.Visibility!=visibility)Shape.Visibility=visibility;
    }
}

public abstract partial class PlotCanvas : Canvas
{
    private bool _queued;
    private readonly List<TextBlock> _labels=new();
    private int _labelCount;
    protected static readonly Color Muted=Color.FromArgb(255,130,142,152);
    private readonly SolidColorBrush _muted=new(Muted);
    private readonly Rectangle _selection=new(){IsHitTestVisible=false,Opacity=.14};
    private readonly ToolTip _tooltip=new();
    private readonly TextBlock _tipText=new(){TextWrapping=TextWrapping.Wrap,MaxWidth=720};
    private int _hoverIndex=-1;
    protected double PlotLeft,PlotTop,PlotWidth,PlotHeight;
    protected DateTimeOffset From,Until;
    internal int RenderCount {get;private set;}
    protected PlotCanvas()
    {
        Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _selection.Fill=_muted;Children.Add(_selection);_selection.Visibility=Visibility.Collapsed;
        _tooltip.Content=_tipText;ToolTipService.SetToolTip(this,_tooltip);
        SizeChanged+=(_,_)=>InvalidatePlot();Loaded+=(_,_)=>InvalidatePlot();
        Unloaded+=(_,_)=>_tooltip.IsOpen=false;
        PointerMoved+=(_,e)=>ShowTip(e.GetCurrentPoint(this).Position.X);
        PointerPressed+=(_,e)=>ShowTip(e.GetCurrentPoint(this).Position.X);
        PointerExited+=(_,_)=>{_tooltip.IsOpen=false;_hoverIndex=-1;};
    }
    public void InvalidatePlot()
    {
        if(_queued)return;_queued=true;
        if(!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,()=>
        {
            _queued=false;if(!IsLoaded)return;
            for(DependencyObject? node=this;node is not null;node=VisualTreeHelper.GetParent(node))
                if(node is UIElement element&&element.Visibility!=Visibility.Visible)return;
            _hoverIndex=-1;if(_tooltip.IsOpen)_tooltip.IsOpen=false;RenderCount++;Draw();
        }))_queued=false;
    }
    protected abstract void Draw();
    protected abstract int HitIndex(double x);
    internal abstract string TipAt(int index);
    private void ShowTip(double x)
    {
        int index=HitIndex(x);if(index<0){_tooltip.IsOpen=false;return;}
        if(index!=_hoverIndex){_hoverIndex=index;_tipText.Text=TipAt(index);}
        if(_tipText.Text.Length>0)_tooltip.IsOpen=true;
    }
    protected double X(DateTimeOffset time)=>PlotLeft+Math.Clamp((time-From).TotalMilliseconds/Math.Max(1,(Until-From).TotalMilliseconds),0,1)*PlotWidth;
    protected int BucketAt(double x,IReadOnlyList<TcpLatencyMonitor.Core.Bucket> points)
    {
        if(points.Count==0||x<PlotLeft||x>PlotLeft+PlotWidth)return -1;
        double fraction=Math.Clamp((x-PlotLeft)/Math.Max(1,PlotWidth),0,1);
        var time=From.AddTicks((long)((Until-From).Ticks*fraction));int lo=0,hi=points.Count-1;
        while(lo<hi){int mid=(lo+hi+1)/2;if(points[mid].Start<=time)lo=mid;else hi=mid-1;}return lo;
    }
    protected void BeginLabels()=>_labelCount=0;
    protected void Label(string text,double x,double y,int size=10)
    {
        if(_labelCount==_labels.Count){var label=new TextBlock{Foreground=_muted,IsHitTestVisible=false};_labels.Add(label);Children.Add(label);}
        var item=_labels[_labelCount++];if(item.Text!=text)item.Text=text;if(item.FontSize!=size)item.FontSize=size;item.Visibility=Visibility.Visible;if(GetLeft(item)!=x)SetLeft(item,x);if(GetTop(item)!=y)SetTop(item,y);
    }
    protected void EndLabels(){for(int i=_labelCount;i<_labels.Count;i++)_labels[i].Visibility=Visibility.Collapsed;}
    protected void Selection(DateTimeOffset? from,DateTimeOffset? until)
    {
        bool visible=from.HasValue&&until.HasValue&&until>From&&from<Until;
        var visibility=visible?Visibility.Visible:Visibility.Collapsed;if(_selection.Visibility!=visibility)_selection.Visibility=visibility;
        if(!visible)return;SetLeft(_selection,X(from!.Value));SetTop(_selection,PlotTop);_selection.Width=Math.Max(0,X(until!.Value)-X(from.Value));_selection.Height=Math.Max(0,PlotHeight);
    }
}
