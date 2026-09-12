using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TcpLatencyMonitor.Core;
using Windows.UI;

namespace TcpLatencyMonitor.App.Controls;

// Cells are owned by their current row/column. A tap reads the updated payload.
internal sealed class HourMatrix
{
    internal sealed class Cell
    {
        public Border Tile {get;}=new(){CornerRadius=new CornerRadius(3)};
        public TextBlock Marker {get;}=new(){FontSize=12,Foreground=new SolidColorBrush(Microsoft.UI.Colors.White),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        public SolidColorBrush Brush {get;}=new();
        public TextBlock TipText {get;}=new(){TextWrapping=TextWrapping.Wrap,MaxWidth=720};
        public ToolTip Tip {get;}=new();
        public DateTimeOffset From,Until;
        public string Description="";
        public Bucket? Bucket;
        public string Name="";
        public Cell(){Tile.Child=Marker;Tile.Background=Brush;Tip.Content=TipText;ToolTipService.SetToolTip(Tile,Tip);Tile.PointerEntered+=(_,_)=>RefreshTip();Tip.Opened+=(_,_)=>RefreshTip();}
        internal void RefreshTip(){if(TipText.Text!=Description)TipText.Text=Description;}
    }
    private readonly Grid _grid;
    private readonly Action<DateTimeOffset,DateTimeOffset,string> _select;
    private readonly List<TextBlock> _dates=new();
    internal readonly Dictionary<int,Cell> Cells=new();
    public HourMatrix(Grid grid,Action<DateTimeOffset,DateTimeOffset,string> select){_grid=grid;_select=select;}
    public void Clear(){_grid.Children.Clear();_grid.ColumnDefinitions.Clear();_grid.RowDefinitions.Clear();Cells.Clear();_dates.Clear();}
    public void Update(Timeline timeline,bool loss,double min,double max,string name="")
    {
        if(_grid.ColumnDefinitions.Count==0)
        {
            _grid.ColumnDefinitions.Add(new(){Width=new GridLength(68)});for(int i=0;i<24;i++)_grid.ColumnDefinitions.Add(new());
            _grid.RowDefinitions.Add(new(){Height=new GridLength(17)});
            for(int h=0;h<24;h++){var text=new TextBlock{Text=h.ToString("00"),FontSize=9,HorizontalAlignment=HorizontalAlignment.Center};Grid.SetColumn(text,h+1);_grid.Children.Add(text);}
        }
        int row=0;var used=new HashSet<int>();
        // Include the UTC offset so repeated local hours during DST never overlap.
        foreach(var group in timeline.Hours.GroupBy(b=>(b.Start.ToLocalTime().Date,b.Start.ToLocalTime().Offset)))
        {
            row++;
            if(_grid.RowDefinitions.Count<=row)_grid.RowDefinitions.Add(new(){Height=new GridLength(19)});
            if(_dates.Count<row){var date=new TextBlock{FontSize=9,VerticalAlignment=VerticalAlignment.Center};_dates.Add(date);Grid.SetRow(date,row);_grid.Children.Add(date);}
            var dateText=group.Key.Date.ToString("MM-dd ddd");if(_dates[row-1].Text!=dateText)_dates[row-1].Text=dateText;
            foreach(var bucket in group)
            {
                int column=bucket.Start.ToLocalTime().Hour+1,key=row*25+column;used.Add(key);
                if(!Cells.TryGetValue(key,out var cell))
                {
                    cell=new();Cells.Add(key,cell);Grid.SetRow(cell.Tile,row);Grid.SetColumn(cell.Tile,column);_grid.Children.Add(cell.Tile);
                    int currentKey=key;cell.Tile.Tapped+=(_,_)=>Select(currentKey);
                }
                bool empty=bucket.Attempts+bucket.LocalErrors==0;
                double ratio=loss?(bucket.Attempts==0?0:(bucket.Attempts-bucket.Successes)/(double)bucket.Attempts):max-min<.001?0:(bucket.Average.GetValueOrDefault()-min)/(max-min);
                ratio=Math.Clamp(ratio,0,1);
                var color=empty?Color.FromArgb(255,169,177,186):bucket.Attempts==0?Color.FromArgb(255,135,144,168):bucket.Successes==0?Color.FromArgb(255,201,64,65):Color.FromArgb(255,(byte)(31+ratio*205),(byte)(158-ratio*73),(byte)(149-ratio*95));
                var from=bucket.Start<timeline.From?timeline.From:bucket.Start;var until=bucket.Start.AddHours(1)>timeline.Until?timeline.Until:bucket.Start.AddHours(1);
                if(cell.Bucket==bucket&&cell.From==from&&cell.Until==until&&cell.Name==name&&cell.Brush.Color==color)continue;
                cell.Bucket=bucket;cell.Name=name;
                if(cell.Brush.Color!=color)cell.Brush.Color=color;
                cell.Tile.Opacity=empty?.2:1;
                cell.Marker.Text=empty?"":bucket.Attempts==0?"!":bucket.Successes==0?"×":bucket.Attempts>bucket.Successes?"·":"";
                cell.From=from;cell.Until=until;
                static string Ms(double? n)=>n.HasValue?$"{n:F1} ms":"—";
                string description=(name.Length==0?"":name+" · ")+$"{cell.From.ToLocalTime():MM-dd HH:mm}—{cell.Until.ToLocalTime():MM-dd HH:mm} ({cell.From.ToLocalTime():zzz})\n平均 {Ms(bucket.Average)} · 最低 {Ms(bucket.Minimum)} · 最高 {Ms(bucket.Maximum)}\n成功 {bucket.Successes}/{bucket.Attempts} · 本地错误 {bucket.LocalErrors}"+(empty?" · 未采样":"")+(cell.Until-cell.From<TimeSpan.FromHours(1)?" · 部分小时":"");
                if(cell.Description!=description){cell.Description=description;if(cell.Tip.IsOpen)cell.RefreshTip();}
            }
        }
        foreach(int key in Cells.Keys.Where(k=>!used.Contains(k)).ToArray()){_grid.Children.Remove(Cells[key].Tile);Cells.Remove(key);}
        while(_dates.Count>row){_grid.Children.Remove(_dates[^1]);_dates.RemoveAt(_dates.Count-1);}
        while(_grid.RowDefinitions.Count>row+1)_grid.RowDefinitions.RemoveAt(_grid.RowDefinitions.Count-1);
    }
    internal void Select(int key){if(Cells.TryGetValue(key,out var cell))_select(cell.From,cell.Until,cell.Description);}
}
