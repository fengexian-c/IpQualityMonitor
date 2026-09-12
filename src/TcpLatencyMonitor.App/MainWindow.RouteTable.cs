using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TcpLatencyMonitor.Core;

namespace TcpLatencyMonitor.App;

public sealed partial class MainWindow
{
    private RouteRun? _overviewTableRun;
    private RouteAnnotation? _overviewTableAnnotation;
    private RouteTableModel? _overviewTableModel;
    private readonly HashSet<string> _expandedOverviewGroups=new();

    private void SetOverviewTable(RouteRun? run,RouteAnnotation? annotation)
    {
        if(_overviewTableRun?.Id!=run?.Id)_expandedOverviewGroups.Clear();
        _overviewTableRun=run;_overviewTableAnnotation=annotation;RenderOverviewTable();
    }
    private void OverviewNodes_Click(object sender,RoutedEventArgs e){_expandedOverviewGroups.Clear();RenderOverviewTable();}
    private static string OverviewGroupKey(RouteTableRow row)=>$"{row.First}:{row.Last}:"+string.Join(",",row.Addresses);
    private void ToggleOverviewGroup(RouteTableRow row)
    {
        string key=OverviewGroupKey(row);if(!_expandedOverviewGroups.Remove(key))_expandedOverviewGroups.Add(key);RenderOverviewTable();
    }
    private static string RouteRowDetails(RouteTableRow row)=>
        (row.Merged?"合并范围：各跳平均 RTT 的最小～最大值；* 表示部分节点无有效 RTT。\n":"RTT 为本次路由探测的有效回应均值。\n")+
        string.Join("\n\n",row.Nodes.Select(n=>$"第 {n.Ttl} 跳 · {n.Address??"—"}{(n.Target?"（目标）":"")}\n"+
            $"{n.Location} · {n.Network}\n均值 {RouteTableRow.FormatRtt(n.AverageRtt)} ms · 该 IP 有效回应 {n.ReplyCount} / 本跳探测 {n.Attempts}\n"+
            string.Join(" / ",n.Probes.Select(p=>$"{(p.IsSupplemental?"补测":"初测")} #{p.Sequence} {p.Label} {RouteTableRow.FormatRtt(p.RttMs)} ms"))));
    private void RenderOverviewTable()
    {
        if(OverviewRouteGrid is null)return;
        var grid=OverviewRouteGrid;grid.Children.Clear();grid.RowDefinitions.Clear();grid.ColumnDefinitions.Clear();
        foreach(var width in new[]{new GridLength(38),new GridLength(1.5,GridUnitType.Star),new GridLength(1,GridUnitType.Star),new GridLength(1.4,GridUnitType.Star),new GridLength(92)})
            grid.ColumnDefinitions.Add(new ColumnDefinition{Width=width});
        grid.ColumnSpacing=8;
        grid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(24)});
        void Cell(string text,int row,int column,bool mono=false,bool right=false,bool header=false)
        {
            var block=new TextBlock{Text=text,FontSize=11,VerticalAlignment=VerticalAlignment.Center,
                TextTrimming=TextTrimming.CharacterEllipsis,MaxLines=1,IsTextSelectionEnabled=!header,
                HorizontalAlignment=right?HorizontalAlignment.Right:HorizontalAlignment.Stretch};
            if(mono)block.FontFamily=new FontFamily("Consolas");
            if(header)block.Foreground=(SolidColorBrush)Application.Current.Resources["SecondaryTextBrush"];
            Grid.SetRow(block,row);Grid.SetColumn(block,column);grid.Children.Add(block);
            if(!header)ToolTipService.SetToolTip(block,text);
        }
        string[] headers={"跳数","IP","地区（IP 定位）","网络 / 线路","RTT（ms）"};
        for(int i=0;i<headers.Length;i++)Cell(headers[i],0,i,right:i==4,header:true);
        OverviewAllNodes.IsEnabled=_overviewTableRun is not null;
        if(_overviewTableRun is null||_overviewTableAnnotation is null)
        {
            _overviewTableModel=null;OverviewTableInfo.Text="暂无路由记录";return;
        }
        _overviewTableModel=RouteTable.Build(_overviewTableRun,_overviewTableAnnotation,OverviewAllNodes.IsChecked==true);
        var model=_overviewTableModel;
        var homonyms=model.Rows.SelectMany(r=>r.Nodes).Where(n=>n.Annotation?.Identity?.Location.Level!="unknown"&&n.Annotation is not null)
            .GroupBy(n=>n.Location).Where(g=>g.Select(n=>n.Annotation!.Identity?.Location.Key).Distinct().Count()>1).Select(g=>g.Key).ToHashSet();
        int rowIndex=0;
        void AddRow(RouteTableRow row,bool child=false)
        {
            int index=++rowIndex;grid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(25)});
            var backdrop=new Border{CornerRadius=new CornerRadius(3),Background=index%2==0?(SolidColorBrush)Application.Current.Resources["PanelBackgroundBrush"]:null};
            Grid.SetRow(backdrop,index);Grid.SetColumnSpan(backdrop,5);grid.Children.Add(backdrop);
            var node=row.Nodes[0];string detail=RouteRowDetails(row);
            Cell((child?"↳ ":"")+row.Hops,index,0);
            if(row.Merged)
            {
                bool expanded=_expandedOverviewGroups.Contains(OverviewGroupKey(row));
                string label=(expanded?"▾ ":"▸ ")+(row.Addresses.Length==1?row.Addresses[0]:$"{row.Addresses.Length} 个 IP")+(expanded?" · 收起":" · 展开");
                var button=new Button{Content=new TextBlock{Text=label,FontSize=11,TextTrimming=TextTrimming.CharacterEllipsis},
                    MinWidth=0,MinHeight=0,Height=24,Padding=new Thickness(0),BorderThickness=new Thickness(0),
                    Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left};
                button.Click+=(_,_)=>ToggleOverviewGroup(row);Grid.SetRow(button,index);Grid.SetColumn(button,1);grid.Children.Add(button);ToolTipService.SetToolTip(button,detail);
            }
            else Cell(node.Address??"—",index,1,mono:true);
            string place=node.NonPublic?"—":node.Location;
            if(homonyms.Contains(place)&&node.Annotation?.Metadata is NodeMetadata data)
                place=string.Join(" / ",new[]{data.Country,data.Region,place}.Where(s=>s.Length>0).Distinct());
            if(node.Address is not null&&!node.NonPublic&&node.Annotation?.Identity?.Location is {} location)
            {
                string address=node.Address;
                var locationButton=new Button{Content=new TextBlock{Text=place+(location.Marker.Length>0?" · "+location.Marker:""),FontSize=11,TextTrimming=TextTrimming.CharacterEllipsis},
                    Padding=new Thickness(0),MinHeight=0,MinWidth=0,Height=24,BorderThickness=new Thickness(0),Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left};
                locationButton.Click+=async(_,_)=>await ShowNodeCalibrationAsync(address,node.Annotation?.Metadata);
                Grid.SetRow(locationButton,index);Grid.SetColumn(locationButton,2);grid.Children.Add(locationButton);
                ToolTipService.SetToolTip(locationButton,new TextBlock{Text=location.Details+"\n点击查看依据 / 校准",MaxWidth=560,TextWrapping=TextWrapping.Wrap});
            }
            else Cell(place,index,2);
            string network=node.Network;
            if(node.Annotation?.Identity?.Kind is "prefix" or "prefix-with-asn")network+="（地址段）";
            if(node.State=="error"&&node.Address is not null)network+=" · 诊断错误回复";
            if(node.MultipleAddresses)network+=" · 多响应";
            if(node.Probes.Any(p=>p.IsSupplemental))network+=" · 补测";
            if(row.Target)network+=" · 目标";
            Cell(network,index,3);Cell(row.Rtt,index,4,mono:true,right:true);
            foreach(var element in grid.Children.OfType<FrameworkElement>().Where(e=>Grid.GetRow(e)==index&&Grid.GetColumn(e)==4))
                ToolTipService.SetToolTip(element,new TextBlock{Text=detail,TextWrapping=TextWrapping.Wrap,MaxWidth=560});
            if(row.Merged&&_expandedOverviewGroups.Contains(OverviewGroupKey(row)))foreach(var member in row.Nodes)AddRow(new(new[]{member}),true);
        }
        foreach(var row in model.Rows)AddRow(row);
        if(model.Rows.Count==0)
        {
            grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var empty=new TextBlock{Text="暂无可显示的公网响应节点；可勾选右上角“显示全部节点”。",FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,6)};
            Grid.SetRow(empty,1);Grid.SetColumnSpan(empty,5);grid.Children.Add(empty);
        }
        OverviewTableInfo.Text=$"隐藏：非公网 {model.HiddenNonPublic} 个 · 未回应 {model.HiddenNoReply} 跳"+
            (model.MergedNodes>0?$" · 合并减少 {model.MergedNodes} 行":"")+(!_overviewTableRun.Reached?" · 未到达目标":"");
    }
}
