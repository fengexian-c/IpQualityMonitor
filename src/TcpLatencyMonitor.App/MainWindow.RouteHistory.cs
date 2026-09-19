using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TcpLatencyMonitor.Core;

namespace TcpLatencyMonitor.App;

public sealed partial class MainWindow
{
    private RouteHistoryIndex? _routeHistory;
    private RouteAnalysisService? _routeAnalysis;
    private string? _routeHistoryTarget,_routeHistoryStamp;
    private CancellationTokenSource? _routeHistoryCancellation;
    private CancellationTokenSource? _routeMatrixMetadataCancellation;
    private IReadOnlyList<RouteMembership> _routeHistoryWindow=Array.Empty<RouteMembership>();
    private Dictionary<string,NodeMetadata> _routeHistoryMetadata=new();
    private readonly string?[] _routeHistoryColumns=new string?[3];
    private readonly List<ComboBox> _routeColumnPickers=new();
    private readonly List<TextBlock> _routeColumnSummaries=new();
    private readonly List<StackPanel> _routeColumnPanels=new();
    private readonly List<List<ComboBoxItem>> _routeColumnChoices=new();
    private readonly List<ComboBoxItem> _routeRecordChoices=new();
    private readonly Dictionary<string,string> _routePatternNames=new();
    private readonly Dictionary<string,string> _routeRepresentatives=new();
    private readonly List<(TextBlock Ttl,Border Row,List<(Button Button,TextBlock Text)> Cells)> _routeMatrixRows=new();
    private readonly List<Rectangle> _routeTimeMarks=new();
    private readonly List<TextBlock> _routeTimeLaneLabels=new();
    private DateTimeOffset _routeHistoryUntil;
    private string? _routeCellRun,_routeReferenceRun,_routeRawRequestedId,_routeTrendTarget;
    private bool _routeRawIsReference;
    private Dictionary<(string Id,int Ttl),RouteCellReference?> _routeVisibleReferences=new();
    private Task _routeMatrixTask=Task.CompletedTask;
    private int _routeRecordPage;
    private int _routeCataloguePage;
    private int _routePatternNumber;
    private int _routeMetadataVersion;
    private bool _routeHistoryUpdating,_routeReading;
    private int _routeReadingVersion;
    private bool _routeHistoryReading {get=>_routeReading;set{if(_routeReading!=value){_routeReading=value;_routeReadingVersion++;}}}
    private int _routeHistoryLoadVersion,_routeRawVersion;
    private Timeline? _routeHistoryTrend;
    private DateTimeOffset _routeHistoryChecked;

    private void InitializeRouteAnalysis()
    {
        _routeAnalysis=new(_history,System.IO.Path.Combine(Services.AppPaths.DataDirectory,"route-analysis.db"));
        _routeAnalysis.Updated+=target=>DispatcherQueue.TryEnqueue(()=>{if(!_stopping&&RouteTarget?.Key==target)_recordsDirty=true;});
        _routeAnalysis.Failed+=ex=>Services.StartupDiagnostics.Write("Optional route analysis failed; raw probes continue.",ex);
    }
    private void ResetRawRouteContent(bool close=true)
    {
        CancelMetadata();_routeRawVersion++;_routeRawRequestedId=null;_routeRawIsReference=false;_displayedRoute=null;
        RouteRows.Children.Clear();RouteDetails.Text=RouteAnnotationSummary.Text=RouteAnnotationEvidence.Text="";
        if(close)RouteRawPanel.Visibility=Visibility.Collapsed;
    }
    private void ResetRouteHistoryView()
    {
        CancelRouteHistoryLoad();ResetRawRouteContent();_routeHistoryReading=false;
        _routeHistory=null;_routeHistoryTarget=_routeHistoryStamp=_routeTrendTarget=null;_routeHistoryTrend=null;
        _routeHistoryWindow=Array.Empty<RouteMembership>();_routeHistoryMetadata.Clear();_routeVisibleReferences.Clear();
        _routePatternNames.Clear();_routeRepresentatives.Clear();_routePatternNumber=0;_routeRecordPage=_routeCataloguePage=0;Array.Clear(_routeHistoryColumns);
        _routeCellRun=_routeReferenceRun=null;RouteCellOriginal.IsEnabled=RouteCellReferenceOriginal.IsEnabled=false;
        RouteCellDetails.Text="点击节点查看本次探测与参考出处。";RouteMatrixInfo.Text="正在读取当前目标…";RouteHistoryVersion.Text=RouteTimeLabels.Text="";
        RouteHistoryInfo.Text="正在读取路由历史…";RouteHistoryUpdate.Visibility=RoutePatternPages.Visibility=Visibility.Collapsed;
        _loadingRoutes=true;RouteSelector.Items.Clear();foreach(var choice in _routeRecordChoices){choice.Tag=null;choice.Content=null;}_loadingRoutes=false;SetText(RouteRecordPage,"暂无记录");
        RouteRecordPrevious.IsEnabled=RouteRecordNext.IsEnabled=false;RouteHistoryChart.SetData(Array.Empty<Bucket>());
        foreach(var row in _routeMatrixRows){row.Row.Visibility=row.Ttl.Visibility=Visibility.Collapsed;foreach(var cell in row.Cells){cell.Button.Visibility=Visibility.Collapsed;cell.Button.Tag=null;cell.Text.Text="";}}
        foreach(var panel in _routeColumnPanels)panel.Visibility=Visibility.Collapsed;
        foreach(var mark in _routeTimeMarks)mark.Visibility=Visibility.Collapsed;
        foreach(var label in _routeTimeLaneLabels)label.Text="";
    }

    private void CancelRouteHistoryLoad(){_routeHistoryCancellation?.Cancel();_routeMatrixMetadataCancellation?.Cancel();_routeHistoryLoadVersion++;_routeMetadataVersion++;_routeRawVersion++;}
    private async Task RefreshRouteHistoryAsync(bool force=false)
    {
        var target=RouteTarget;if(target is null)return;
        if(_routeHistoryTarget is not null&&_routeHistoryTarget!=target.Key)ResetRouteHistoryView();
        if(!force&&!_recordsDirty&&_routeHistoryTarget==target.Key&&DateTimeOffset.UtcNow-_routeHistoryChecked<TimeSpan.FromSeconds(15))return;
        _routeHistoryCancellation?.Cancel();_routeHistoryCancellation?.Dispose();
        var cancellation=new CancellationTokenSource();_routeHistoryCancellation=cancellation;var token=cancellation.Token;
        int version=++_routeHistoryLoadVersion,selection=_selectionVersion,days=_timelineDays,readingVersion=_routeReadingVersion;var now=DateTimeOffset.UtcNow;
        bool frozen=_routeHistoryReading&&!force&&_routeHistoryTarget==target.Key&&_routeHistory is not null;
        var until=frozen?_routeHistoryUntil:now;var trendTarget=_target??target;
        _routeHistoryChecked=now;
        if(!frozen)RouteHistoryInfo.Text="正在整理路由历史…";
        try
        {
            var latest=_routeAnalysis is null?await Task.Run(()=>_history.LoadRouteHistory(target,token),token):await _routeAnalysis.GetAsync(target,token);
            bool changed=_routeHistory is null||_routeHistoryTarget!=target.Key||_routeHistoryStamp!=latest.Revision;
            var index=frozen?_routeHistory!:latest;
            var trend=await Task.Run(()=>_history.LoadTimeline(trendTarget,until,days),token);
            if(token.IsCancellationRequested||version!=_routeHistoryLoadVersion||selection!=_selectionVersion||readingVersion!=_routeReadingVersion||days!=_timelineDays||RouteTarget?.Key!=target.Key||_target?.Key!=trendTarget.Key||_hidden||_workspaceView!=1||PageBox.SelectedIndex!=1)return;
            bool redraw=!frozen&&(changed||force)||_routeHistoryTrend?.Days!=days||_routeHistoryWindow.Count==0;
            if(!ReferenceEquals(_routeHistory,index))_routeVisibleReferences.Clear();
            _routeHistory=index;_routeHistoryTarget=target.Key;_routeHistoryStamp=index.Revision;_routeHistoryUntil=until;_routeHistoryTrend=trend;_routeTrendTarget=trendTarget.Key;
            RouteHistoryUpdate.Visibility=frozen?Visibility.Visible:Visibility.Collapsed;
            RouteHistoryUpdate.Content=frozen&&changed?"有更新 · 更新至最新":"更新至最新";
            if(redraw||index.Window(until.AddDays(-days),until).Count!=_routeHistoryWindow.Count)RenderRouteHistory();
            else{RouteHistoryChart.SetData(trend.Points,trend.StepMinutes,trend.Until);RenderRouteTimeAxis();RenderRouteHistoryInfo();_routeMatrixTask=RefreshRouteMatrixMetadataAsync();}
            if(_routeRawRequestedId is string rawId&&!latest.Memberships.ContainsKey(rawId))
            {ResetRawRouteContent();RouteCellDetails.Text="已打开的原始快照已超出保留期。";}
            if(_routeHistoryWindow.Count==0&&_displayedRoute is null&&_routeRawRequestedId is null)
            {ResetRawRouteContent();_routeCellRun=_routeReferenceRun=null;RouteCellOriginal.IsEnabled=RouteCellReferenceOriginal.IsEnabled=false;RouteCellDetails.Text="此时间范围暂无原始路由。";}
            _recordsDirty=false;
        }
        catch(OperationCanceledException){}
        catch(Exception ex)
        {
            if(version!=_routeHistoryLoadVersion||selection!=_selectionVersion)return;
            RouteHistoryInfo.Text="路由分析暂不可用；原始快照仍可查看，采样继续。";RouteHistoryUpdate.Visibility=Visibility.Visible;Services.StartupDiagnostics.Write("Route history projection failed.",ex);
            if(_routeHistory is null)
            {
                ResetRawRouteContent();var fallback=await Task.Run(()=>_history.LoadRoutes(target,100));
                if(version!=_routeHistoryLoadVersion||selection!=_selectionVersion||RouteTarget?.Key!=target.Key)return;
                _loadingRoutes=true;RouteSelector.Items.Clear();foreach(var run in fallback)RouteSelector.Items.Add(new ComboBoxItem{Content=$"{run.Started.ToLocalTime():MM-dd HH:mm:ss} · 原始快照",Tag=run.Id});_loadingRoutes=false;
                _routeCellRun=fallback.FirstOrDefault()?.Id;RouteCellOriginal.IsEnabled=_routeCellRun is not null;RouteRecordPage.Text=$"分析降级 · 最近 {fallback.Count} 条原始记录";
            }
        }
    }
    private string PatternName(string? id)=>id is null?"未归类":_routePatternNames.GetValueOrDefault(id,"路径 "+id);
    private async void RouteHistoryUpdate_Click(object sender,RoutedEventArgs e)=>await FollowLatestRouteHistoryAsync();
    private async Task FollowLatestRouteHistoryAsync()
    {
        _routeHistoryReading=false;_routeRepresentatives.Clear();_routeCellRun=_routeReferenceRun=null;
        RouteCellOriginal.IsEnabled=RouteCellReferenceOriginal.IsEnabled=false;ResetRawRouteContent();
        RouteCellDetails.Text="已恢复跟随最新观测；点击节点可查看本次探测与参考出处。";
        await RefreshRouteHistoryAsync(true);
    }
    private void RouteHistoryDisplay_Click(object sender,RoutedEventArgs e){RenderRouteMatrix();}
    private void EnsureRouteMatrix()
    {
        if(_routeColumnPickers.Count>0)return;
        RouteHistoryGrid.ColumnSpacing=8;RouteHistoryGrid.RowSpacing=1;
        RouteHistoryGrid.ColumnDefinitions.Add(new(){Width=new GridLength(38)});
        for(int i=0;i<3;i++)RouteHistoryGrid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star),MinWidth=225});
        RouteHistoryGrid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        for(int i=0;i<3;i++)
        {
            int column=i;var panel=new StackPanel{Spacing=5,Margin=new Thickness(4,0,4,10)};
            var picker=new ComboBox{HorizontalAlignment=HorizontalAlignment.Stretch,FontSize=12,MinWidth=0};
            picker.SelectionChanged+=(_,_)=>
            {
                if(_routeHistoryUpdating)return;
                var id=(picker.SelectedItem as ComboBoxItem)?.Tag as string;
                if(id is not null&&_routeHistoryColumns.Where((_,n)=>n!=column).Contains(id))
                {RouteHistoryInfo.Text="该路径已在其他列显示。";RenderRouteHistory();return;}
                _routeHistoryColumns[column]=id;_routeHistoryReading=true;RenderRouteMatrix();RenderRouteTimeAxis();_routeMatrixTask=RefreshRouteMatrixMetadataAsync();
            };
            var summary=new TextBlock{FontSize=11,TextWrapping=TextWrapping.Wrap,Foreground=SecondaryBrush};
            panel.Children.Add(picker);panel.Children.Add(summary);Grid.SetColumn(panel,i+1);RouteHistoryGrid.Children.Add(panel);
            _routeColumnPickers.Add(picker);_routeColumnSummaries.Add(summary);_routeColumnPanels.Add(panel);_routeColumnChoices.Add(new());
        }
    }
    private void RenderRouteHistory()
    {
        if(_routeHistory is null||PageBox.SelectedIndex!=1||_hidden)return;
        EnsureRouteMatrix();var index=_routeHistory;
        var validIds=index.Patterns.Select(p=>p.Id).ToHashSet();
        foreach(var id in _routePatternNames.Keys.Where(id=>!validIds.Contains(id)).ToArray()){_routePatternNames.Remove(id);_routeRepresentatives.Remove(id);}
        foreach(var group in index.Memberships.Values.Where(m=>m.PatternId is not null).GroupBy(m=>m.PatternId!).OrderBy(g=>g.Min(m=>m.Observation.Finished)).ThenBy(g=>g.Key,StringComparer.Ordinal))
            if(!_routePatternNames.ContainsKey(group.Key))_routePatternNames[group.Key]="路径 "+(++_routePatternNumber);
        _routeHistoryWindow=index.Window(_routeHistoryUntil.AddDays(-_timelineDays),_routeHistoryUntil);
        var groups=_routeHistoryWindow.Where(m=>m.PatternId is not null).GroupBy(m=>m.PatternId!).OrderByDescending(g=>g.Max(m=>m.Observation.Finished)).ToArray();
        var available=groups.Select(g=>g.Key).ToArray();
        for(int c=0;c<3;c++)if(_routeHistoryColumns[c] is null||!available.Contains(_routeHistoryColumns[c]))
            _routeHistoryColumns[c]=available.FirstOrDefault(id=>!_routeHistoryColumns.Contains(id));
        _routeCataloguePage=Math.Clamp(_routeCataloguePage,0,Math.Max(0,(groups.Length-1)/80));
        var choices=available.Skip(_routeCataloguePage*80).Take(80).Concat(_routeHistoryColumns.Where(id=>id is not null).Select(id=>id!)).Distinct().ToArray();
        RoutePatternPages.Visibility=groups.Length>80?Visibility.Visible:Visibility.Collapsed;
        RoutePatternPageText.Text=$"路径目录 {_routeCataloguePage+1}/{Math.Max(1,(groups.Length+79)/80)} 页";
        RoutePatternPrevious.IsEnabled=_routeCataloguePage>0;RoutePatternNext.IsEnabled=(_routeCataloguePage+1)*80<groups.Length;
        var counts=groups.ToDictionary(g=>g.Key,g=>g.Count());
        var options=choices.Select(id=>(Id:id,Caption:$"{PatternName(id)} · {counts[id]} 条")).ToArray();
        _routeHistoryUpdating=true;
        for(int c=0;c<3;c++)
        {
            SetRouteChoices(_routeColumnPickers[c],_routeColumnChoices[c],options,_routeHistoryColumns[c]);
        }
        _routeHistoryUpdating=false;
        RenderRouteHistoryInfo();
        SetText(RouteHistoryVersion,$"按保留期内原始记录归类 · {RouteHistoryIndex.AlgorithmVersion} / {index.Revision} · 历史参考最多相隔 24 小时 · IP 定位与线路注释不参与路径身份判断");
        if(_routeHistoryTrend is {} trend)RouteHistoryChart.SetData(trend.Points,trend.StepMinutes,trend.Until);
        RenderRouteMatrix();RenderRouteTimeAxis();RenderRouteRecordSelector();_routeMatrixTask=RefreshRouteMatrixMetadataAsync();
    }
    private void RenderRouteHistoryInfo()
    {
        int temporal=_routeHistoryWindow.Count(m=>m.Kind==RouteMembershipKind.Temporal),unknown=_routeHistoryWindow.Count(m=>m.PatternId is null);
        int groups=_routeHistoryWindow.Where(m=>m.PatternId is not null).Select(m=>m.PatternId).Distinct().Count();
        SetText(RouteHistoryInfo,$"近 {(_timelineDays==1?"24 小时":"7 天")} · {groups} 种可见路径 · {_routeHistoryWindow.Count} 条记录（时间推定 {temporal} / 未归类 {unknown}） · 截至 {_routeHistoryUntil.ToLocalTime():HH:mm:ss}");
    }
    private async Task RefreshRouteMatrixMetadataAsync()
    {
        if(_routeHistory is null||_hidden||PageBox.SelectedIndex!=1)return;
        _routeMatrixMetadataCancellation?.Cancel();_routeMatrixMetadataCancellation?.Dispose();_routeMatrixMetadataCancellation=new();var token=_routeMatrixMetadataCancellation.Token;
        int version=++_routeMetadataVersion,selection=_selectionVersion;var index=_routeHistory;
        var members=_routeHistoryColumns.Select(Representative).Where(m=>m is not null).Select(m=>m!).ToArray();
        try
        {
            var result=await Task.Run(()=>
            {
                var ips=new HashSet<string>();var references=new Dictionary<(string,int),RouteCellReference?>();
                foreach(var member in members)foreach(var hop in member.Observation.Hops.Values)
                {
                    token.ThrowIfCancellationRequested();foreach(var address in hop.Addresses)ips.Add(address.Address);
                    var reference=index.Reference(member,hop.Ttl);references[(member.Observation.Id,hop.Ttl)]=reference;
                    if(reference is not null)foreach(var address in reference.Hop.Addresses)ips.Add(address.Address);
                }
                return (Metadata:_history.LoadRouteHistoryMetadata(ips,token),References:references);
            },token);
            if(version!=_routeMetadataVersion||selection!=_selectionVersion||!ReferenceEquals(index,_routeHistory)||_hidden||_workspaceView!=1||PageBox.SelectedIndex!=1)return;
            _routeHistoryMetadata=result.Metadata;_routeVisibleReferences=result.References;RenderRouteMatrix();
        }
        catch(OperationCanceledException){}
        catch(Exception ex){Services.StartupDiagnostics.Write("Visible route metadata read failed.",ex);}
    }
    private RouteCellReference? VisibleRouteReference(RouteMembership member,int ttl)=>_routeVisibleReferences.GetValueOrDefault((member.Observation.Id,ttl));
    private string FirstDecisionLabel(RouteMembership member)
    {
        if(_routeHistory is null||!_routeHistory.FirstDecisions.TryGetValue(member.Observation.Id,out var first))return "当时归列未记录";
        string changes=first.Changes(member);return first.Origin+(changes.Length>0?" · "+changes:" · 判断一致");
    }
    private RouteMembership? Representative(string? id)
    {
        if(id is null)return null;
        if(_routeRepresentatives.TryGetValue(id,out var selected)&&_routeHistory?.Memberships.TryGetValue(selected,out var m)==true&&m.PatternId==id&&_routeHistoryWindow.Any(w=>w.Observation.Id==selected))return m;
        return _routeHistoryWindow.LastOrDefault(m=>m.PatternId==id);
    }
    private void RenderRouteMatrix()
    {
        if(_routeHistory is null||_routeColumnPickers.Count==0)return;
        var members=_routeHistoryColumns.Select(Representative).ToArray();
        for(int c=0;c<3;c++)
        {
            var m=members[c];var group=_routeHistoryWindow.Where(x=>x.PatternId==m?.PatternId&&m is not null).ToArray();
            _routeColumnPanels[c].Visibility=m is null?Visibility.Collapsed:Visibility.Visible;
            RouteHistoryGrid.ColumnDefinitions[c+1].MinWidth=m is null?0:225;
            RouteHistoryGrid.ColumnDefinitions[c+1].Width=m is null?new GridLength(0):new GridLength(1,GridUnitType.Star);
            SetText(_routeColumnSummaries[c],m is null?"选择一种路径进行对比":$"代表快照 {m.Observation.Started.ToLocalTime():MM-dd HH:mm:ss}\n{m.Label} · 匹配 {group.Count(x=>x.Kind!=RouteMembershipKind.Temporal)} / 推定 {group.Count(x=>x.Kind==RouteMembershipKind.Temporal)}\n{(m.Observation.Reached?"目标回应":"部分路径")} · {(m.Observation.DestinationTtl is int ttl?$"到达第 {ttl} 跳":"长度未确认")}\n可见观测 {group.First().Observation.Started.ToLocalTime():MM-dd HH:mm} → {group.Last().Observation.Started.ToLocalTime():MM-dd HH:mm}\n{FirstDecisionLabel(m)}");
        }
        int max=members.Where(m=>m is not null).Select(m=>m!.Observation.LastTtl).DefaultIfEmpty(0).Max();
        var visible=new List<(int First,int Last,string? Fold)>();int folded=0;
        for(int ttl=1;ttl<=max;ttl++)
        {
            string? fold=null;
            if(RouteHistoryAllNodes.IsChecked!=true)
            {
                var active=members.Where(m=>m is not null).Select(m=>m!).ToArray();
                var hops=active.Select(m=>m.Observation.Hops.GetValueOrDefault(ttl)).ToArray();
                if(hops.All(h=>h is not null&&h.Silent)&&active.All(m=>RouteHistoryReferences.IsChecked!=true||VisibleRouteReference(m,ttl) is null))fold="均未回应";
                else if(active.All(m=>m.Observation.DestinationTtl!=ttl)&&hops.All(h=>h is not null&&h.Errors==0&&h.Addresses.Count>0&&h.Addresses.All(a=>NodeMetadataClient.LocalLabel(a.Address) is not null))
                    &&hops.Select(h=>string.Join(',',h!.Addresses.Select(a=>a.Address))).Distinct().Count()==1)fold="共同非公网节点";
            }
            if(fold is not null){folded++;if(visible.Count>0&&visible[^1].Fold==fold&&visible[^1].Last==ttl-1){visible[^1]=(visible[^1].First,ttl,fold);continue;}}
            visible.Add((ttl,ttl,fold));
        }
        while(_routeMatrixRows.Count<visible.Count)
        {
            int row=_routeMatrixRows.Count+1;RouteHistoryGrid.RowDefinitions.Add(new(){Height=GridLength.Auto});
            var bg=new Border{Background=(SolidColorBrush)Application.Current.Resources["PanelBackgroundBrush"],CornerRadius=new CornerRadius(4)};
            Grid.SetRow(bg,row);Grid.SetColumnSpan(bg,4);RouteHistoryGrid.Children.Add(bg);
            var ttlText=new TextBlock{FontSize=11,Margin=new Thickness(2,10,0,0),TextWrapping=TextWrapping.Wrap};Grid.SetRow(ttlText,row);RouteHistoryGrid.Children.Add(ttlText);
            var cells=new List<(Button,TextBlock)>();
            for(int c=0;c<3;c++)
            {
                var text=new TextBlock{FontSize=12,FontFamily=new FontFamily("Consolas, Microsoft YaHei UI"),TextWrapping=TextWrapping.Wrap};
                var button=new Button{Content=text,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Stretch,Padding=new Thickness(8),Margin=new Thickness(0,1,0,1),BorderThickness=new Thickness(0),Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent)};
                button.Click+=RouteCell_Click;Grid.SetRow(button,row);Grid.SetColumn(button,c+1);RouteHistoryGrid.Children.Add(button);cells.Add((button,text));
            }
            _routeMatrixRows.Add((ttlText,bg,cells));
        }
        for(int r=0;r<_routeMatrixRows.Count;r++)
        {
            var widgets=_routeMatrixRows[r];bool show=r<visible.Count;widgets.Ttl.Visibility=widgets.Row.Visibility=show?Visibility.Visible:Visibility.Collapsed;
            foreach(var cell in widgets.Cells)cell.Button.Visibility=show?Visibility.Visible:Visibility.Collapsed;
            if(!show)continue;var row=visible[r];SetText(widgets.Ttl,row.First==row.Last?row.First.ToString():$"{row.First}–{row.Last}");
            for(int c=0;c<3;c++)
            {
                var (button,text)=widgets.Cells[c];var member=members[c];button.Tag=member is null?null:(member,row.First);
                button.Visibility=member is null?Visibility.Collapsed:Visibility.Visible;
                button.IsEnabled=member is not null&&row.Fold is null;
                if(row.Fold is not null){SetText(text,c==0?row.Fold+" · 勾选显示全部节点可查看":"");continue;}
                SetText(text,member is null?"—":RouteMatrixCell(member,row.First));
            }
        }
        SetText(RouteMatrixInfo,max==0?"此时间范围暂无可归类路径。原始记录仍可从下方查看。":$"相同跳数横向对齐 · 折叠 {folded} 跳 · RTT 属于各列代表快照；参考 IP 不改变本次回应数");
    }
    private string RouteMatrixCell(RouteMembership member,int ttl)
    {
        var observation=member.Observation;
        if(!observation.Hops.TryGetValue(ttl,out var hop))return observation.DestinationTtl is int end&&ttl>end?"— 目标之后":"— 未探测";
        var reference=RouteHistoryReferences.IsChecked==true?VisibleRouteReference(member,ttl):null;
        var addresses=reference?.Hop.Addresses??hop.Addresses;
        string text;
        if(addresses.Count==0)text=hop.Errors>0?"探测错误 / 不可达":"未回应";
        else
        {
            text=string.Join("\n",addresses.Take(2).Select(a=>RouteAddressText(a,ttl,observation.Address,reference is null)));
            if(addresses.Count>2)text+=$"\n另 {addresses.Count-2} 个 IP · 点击查看";
            if(reference is null&&addresses.Any(a=>a.Supplemental))text+=" · 本轮补测";
        }
        if(reference is not null)text+=$"\n◇ {(reference.Later?"后续观测":"历史参考")} {reference.Source.Started.ToLocalTime():MM-dd HH:mm}{(reference.Temporal?" · 时间推定":"")} · RTT —";
        text+=$"\n回应 {hop.Replies}/{hop.Attempts} · 超时 {hop.Timeouts}"+(hop.Errors>0?$" · 错误 {hop.Errors}":"");
        return text;
    }
    private string RouteAddressText(RouteObservedAddress address,int ttl,string destination,bool rtt)
    {
        _routeHistoryMetadata.TryGetValue(address.Address,out var metadata);
        var node=NodeClassifier.Classify(ttl,address.Address,address.Address==destination,metadata,DateTimeOffset.UtcNow);
        return address.Address+(rtt?$"    {RouteTableRow.FormatRtt(address.RttMs)} ms":"")+"\n"+(node.Identity?.Location.Label??"地区未知")+" · "+node.Name;
    }
    private void RouteCell_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not Button{Tag:ValueTuple<RouteMembership,int> item})return;
        var (member,ttl)=item;_routeHistoryReading=true;_routeCellRun=member.Observation.Id;
        var reference=RouteHistoryReferences.IsChecked==true?VisibleRouteReference(member,ttl):null;
        _routeReferenceRun=reference?.Source.Id;RouteCellReferenceOriginal.IsEnabled=_routeReferenceRun is not null;
        string text=$"第 {ttl} 跳 · {PatternName(member.PatternId)} · {member.Observation.Started.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n{member.Label}";
        if(member.EvidenceId is string evidence&&_routeHistory!.Memberships.TryGetValue(evidence,out var donor))text+=$" · 依据 {donor.Observation.Started.ToLocalTime():MM-dd HH:mm:ss}，区分节点仍未实测确认";
        text+="\n"+FirstDecisionLabel(member);
        if(_routeHistory!.FirstDecisions.TryGetValue(member.Observation.Id,out var first))
            text+=$"\n首次计算 {first.ComputedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {first.Algorithm} · 证据截至 {first.EvidenceCutoff.ToLocalTime():MM-dd HH:mm:ss}\n首次状态 {first.Kind} · 模式 {first.PatternId??"未归类"} · 依据 {first.EvidenceId??"同次实测"}";
        text+="\n"+RouteMatrixCell(member,ttl);
        if(member.Observation.Hops.TryGetValue(ttl,out var hop)&&hop.Addresses.Count>2)text+="\n全部实测 IP：\n"+string.Join("\n",hop.Addresses.Select(a=>RouteAddressText(a,ttl,member.Observation.Address,true)));
        if(reference is not null)text+=$"\n参考快照：{reference.Source.Id}\n来源实测："+string.Join(" / ",reference.Hop.Addresses.Select(a=>$"{a.Address} {RouteTableRow.FormatRtt(a.RttMs)} ms"))+"\n参考内容未计入本次回应、RTT 或目标到达状态。";
        text+="\n"+member.Observation.Context;RouteCellDetails.Text=text;RouteCellOriginal.IsEnabled=true;
    }
    private void RenderRouteTimeAxis()
    {
        if(_routeHistory is null)return;double width=Math.Max(250,RouteHistoryAxis.ActualWidth)-90;var from=_routeHistoryUntil.AddDays(-_timelineDays);
        int Lane(RouteMembership m)=>m.PatternId is string id&&Array.IndexOf(_routeHistoryColumns,id) is int lane&&lane>=0?lane:3;
        var bins=_routeHistoryWindow.GroupBy(m=>(Bin:Math.Clamp((int)((m.Observation.Started-from).TotalSeconds/(_timelineDays*86400)*240),0,239),Lane:Lane(m))).ToArray();
        while(_routeTimeLaneLabels.Count<4){var label=new TextBlock{FontSize=11,Foreground=SecondaryBrush};RouteHistoryAxis.Children.Add(label);_routeTimeLaneLabels.Add(label);}
        for(int lane=0;lane<4;lane++)
        {
            var label=_routeTimeLaneLabels[lane];SetText(label,lane<3?(_routeHistoryColumns[lane] is string id?PatternName(id):""):"其他 / 未归类");
            Canvas.SetLeft(label,0);Canvas.SetTop(label,lane*19);
        }
        while(_routeTimeMarks.Count<bins.Length){var mark=new Rectangle{Height=12,Width=3};RouteHistoryAxis.Children.Add(mark);_routeTimeMarks.Add(mark);}
        for(int i=0;i<_routeTimeMarks.Count;i++)
        {
            var mark=_routeTimeMarks[i];mark.Visibility=i<bins.Length?Visibility.Visible:Visibility.Collapsed;if(i>=bins.Length)continue;
            var bin=bins[i];mark.Fill=bin.Any(m=>m.Kind==RouteMembershipKind.Temporal)?_warningBrush:bin.All(m=>m.PatternId is null)?SecondaryBrush:Brush(24,157,153);
            Canvas.SetLeft(mark,90+bin.Key.Bin/240d*(width-4));Canvas.SetTop(mark,bin.Key.Lane*19+3);
        }
        SetText(RouteTimeLabels,$"{from.ToLocalTime():MM-dd HH:mm}    →    {_routeHistoryUntil.ToLocalTime():MM-dd HH:mm}  · 点击对应路径的观测点定位；橙色含时间推定，灰色为未归类。空档不代表路径持续。");
    }
    private void RouteHistoryAxis_SizeChanged(object sender,SizeChangedEventArgs e)=>RenderRouteTimeAxis();
    private async void RouteHistoryAxis_PointerPressed(object sender,PointerRoutedEventArgs e)
    {
        if(_routeHistoryWindow.Count==0)return;var point=e.GetCurrentPoint(RouteHistoryAxis).Position;if(point.X<90)return;
        var ratio=Math.Clamp((point.X-90)/Math.Max(1,RouteHistoryAxis.ActualWidth-94),0,1);int lane=Math.Clamp((int)(point.Y/19),0,3);
        var time=_routeHistoryUntil.AddDays(-_timelineDays).AddDays(_timelineDays*ratio);
        var candidates=_routeHistoryWindow.Where(m=>lane<3?m.PatternId is not null&&m.PatternId==_routeHistoryColumns[lane]:m.PatternId is null||!_routeHistoryColumns.Contains(m.PatternId));
        var member=candidates.MinBy(m=>(m.Observation.Started-time).Duration());if(member is null)return;
        await SelectHistoryObservationAsync(member,false);
    }
    private async Task SelectHistoryObservationAsync(RouteMembership member,bool raw)
    {
        _routeHistoryReading=true;_routeCellRun=member.Observation.Id;_routeReferenceRun=null;RouteCellReferenceOriginal.IsEnabled=false;
        if(member.PatternId is string id){if(!_routeHistoryColumns.Contains(id))_routeHistoryColumns[0]=id;_routeRepresentatives[id]=member.Observation.Id;RenderRouteHistory();}
        RouteCellDetails.Text=$"已定位 {member.Observation.Started.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {PatternName(member.PatternId)} · {member.Label}\n{member.Observation.Outcome}\n{FirstDecisionLabel(member)}";
        RouteCellOriginal.IsEnabled=true;
        RouteHistoryChart.SelectInterval(member.Observation.Started,member.Observation.Finished.AddSeconds(1));
        if(raw)await LoadRawRouteAsync(member.Observation.Id);
    }
    private void RenderRouteRecordSelector()
    {
        if(RouteRawPanel.Visibility!=Visibility.Visible)return;
        _loadingRoutes=true;string? selected=(RouteSelector.SelectedItem as ComboBoxItem)?.Tag as string;
        var records=_routeHistoryWindow.Reverse().ToArray();_routeRecordPage=Math.Clamp(_routeRecordPage,0,Math.Max(0,(records.Length-1)/100));
        var choices=records.Skip(_routeRecordPage*100).Take(100).Select(m=>(Id:m.Observation.Id,Caption:$"{m.Observation.Started.ToLocalTime():MM-dd HH:mm:ss} · {PatternName(m.PatternId)} · {m.Label}")).ToArray();
        SetRouteChoices(RouteSelector,_routeRecordChoices,choices,selected);
        SetText(RouteRecordPage,$"第 {_routeRecordPage+1}/{Math.Max(1,(records.Length+99)/100)} 页 · 全部 {records.Length} 条");
        RouteRecordPrevious.IsEnabled=_routeRecordPage>0;RouteRecordNext.IsEnabled=(_routeRecordPage+1)*100<records.Length;_loadingRoutes=false;
    }
    private static void SetRouteChoices(ComboBox picker,List<ComboBoxItem> pool,IReadOnlyList<(string Id,string Caption)> choices,string? selected)
    {
        // WinUI caches the selection-box content. Deselect before changing a reused
        // selected item's caption; otherwise the closed picker can keep stale/blank text.
        if(picker.SelectedItem is ComboBoxItem current)
        {
            int position=pool.IndexOf(current);
            if(position<0||position>=choices.Count||(string?)current.Tag!=choices[position].Id||(string?)current.Content!=choices[position].Caption)
                picker.SelectedIndex=-1;
        }
        while(pool.Count<choices.Count)pool.Add(new ComboBoxItem());
        while(picker.Items.Count>choices.Count)picker.Items.RemoveAt(picker.Items.Count-1);
        while(picker.Items.Count<choices.Count)picker.Items.Add(pool[picker.Items.Count]);
        for(int i=0;i<choices.Count;i++)if(!ReferenceEquals(picker.Items[i],pool[i])){picker.Items.RemoveAt(i);picker.Items.Insert(i,pool[i]);}
        int select=-1;
        for(int i=0;i<pool.Count;i++)
        {
            var item=pool[i];string? id=i<choices.Count?choices[i].Id:null,caption=i<choices.Count?choices[i].Caption:null;
            if((string?)item.Tag!=id)item.Tag=id;if((string?)item.Content!=caption)item.Content=caption;
            if(id is not null&&id==selected)select=i;
        }
        if(picker.SelectedIndex!=select)picker.SelectedIndex=select;
    }
    private void RouteRecordPrevious_Click(object sender,RoutedEventArgs e){_routeRecordPage--;RenderRouteRecordSelector();}
    private void RouteRecordNext_Click(object sender,RoutedEventArgs e){_routeRecordPage++;RenderRouteRecordSelector();}
    private void RoutePatternPrevious_Click(object sender,RoutedEventArgs e){_routeCataloguePage--;_routeHistoryReading=true;RenderRouteHistory();}
    private void RoutePatternNext_Click(object sender,RoutedEventArgs e){_routeCataloguePage++;_routeHistoryReading=true;RenderRouteHistory();}
    private async void RouteRawToggle_Click(object sender,RoutedEventArgs e)
    {
        if(RouteRawPanel.Visibility==Visibility.Visible){_routeRawVersion++;RouteRawPanel.Visibility=Visibility.Collapsed;CancelMetadata();return;}
        var id=_routeCellRun??_routeHistoryWindow.LastOrDefault()?.Observation.Id;
        if(id is not null)await LoadRawRouteAsync(id);else{ResetRawRouteContent();RouteCellDetails.Text="当前目标暂无原始快照。";}
    }
    private async void RouteCellOriginal_Click(object sender,RoutedEventArgs e){if(_routeCellRun is not null)await LoadRawRouteAsync(_routeCellRun);}
    private async void RouteCellReferenceOriginal_Click(object sender,RoutedEventArgs e){if(_routeReferenceRun is not null)await LoadRawRouteAsync(_routeReferenceRun,true);}
    private async Task LoadRawRouteAsync(string id,bool reference=false)
    {
        var target=RouteTarget;if(target is null)return;
        ResetRawRouteContent(false);int version=++_routeRawVersion,selection=_selectionVersion;_routeRawRequestedId=id;_routeRawIsReference=reference;
        RouteRawPanel.Visibility=Visibility.Visible;RouteDetails.Text="正在读取原始快照…";
        if(_routeHistory is not null)RenderRouteRecordSelector();
        try
        {
            var run=await Task.Run(()=>_history.LoadRoute(target,id));
            if(version!=_routeRawVersion||id!=_routeRawRequestedId||selection!=_selectionVersion||target.Key!=RouteTarget?.Key||PageBox.SelectedIndex!=1||_hidden||_workspaceView!=1)return;
            if(run is null){ResetRawRouteContent();RouteCellDetails.Text="原始快照已超出保留期。";return;}
            _routeHistoryReading=true;RenderRoute(run);
        }
        catch(Exception ex){if(version==_routeRawVersion&&selection==_selectionVersion){ResetRawRouteContent();RouteCellDetails.Text="原始快照读取失败，可重试。";ShowError(ex);}}
    }
}
