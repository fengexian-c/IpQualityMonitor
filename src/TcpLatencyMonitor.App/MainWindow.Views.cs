using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TcpLatencyMonitor.Core;

namespace TcpLatencyMonitor.App;

public sealed partial class MainWindow
{
    private NodeMetadataClient? _metadataClient;
    private CancellationTokenSource? _metadataCancellation;
    private RouteRun? _displayedRoute;
    private Task _annotationTask=Task.CompletedTask;
    private void CancelMetadata(){_metadataCancellation?.Cancel();_metadataCancellation?.Dispose();_metadataCancellation=null;}
    private void Metadata_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _settings.EnableNodeMetadata=MetadataBox.IsChecked==true;
            BackgroundMetadataBox.IsEnabled=_settings.EnableNodeMetadata;
            if(!_settings.EnableNodeMetadata)BackgroundMetadataBox.IsChecked=false;
            _settings.BackgroundNodeMetadata=BackgroundMetadataBox.IsChecked==true;
            if(_annotationService is not null)_annotationService.Mode=!_settings.EnableNodeMetadata||!_geoConfigurationValid?0:_settings.BackgroundNodeMetadata?2:1;
            _settings.Save();_lastViewAttemptId=null;
            if(RouteRawPanel.Visibility==Visibility.Visible&&_displayedRoute is not null)RenderRoute(_displayedRoute);
        }
        catch(Exception ex){ShowError(ex);}
    }
    private Target? RouteTarget=>ViewMode=="Both"?_primaryTarget:_target;
    private void Protocol_Changed(object sender,SelectionChangedEventArgs e)=>UpdateProtocolControls();
    private void UpdateProtocolControls()
    {
        if(PortBox is null||ViewProtocolBox is null)return;
        PortBox.Visibility=ProtocolBox.SelectedIndex==0?Visibility.Collapsed:Visibility.Visible;
        LegacyButton.Visibility=ProtocolBox.SelectedIndex==0?Visibility.Collapsed:Visibility.Visible;
        ViewProtocolBox.Visibility=ProtocolBox.SelectedIndex==2?Visibility.Visible:Visibility.Collapsed;
    }
    private async void ViewProtocol_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_selectingProfile||_primaryTarget is null||ViewMode!="Both")return;
        _target=ViewProtocolBox.SelectedIndex==1?_secondaryTarget:_primaryTarget;if(_target is null)return;
        _selectionVersion++;CancelRouteHistoryLoad();_lastSample=null;LatestValue.Text="—";_recordsDirty=true;_timelineKey=null;
        TargetText.Text=_target.Label;AvailabilityLabel.Text=_target.Protocol==ProbeProtocol.Icmp?"近 5 分钟回应率":"近 5 分钟建连成功率";
        if(_sessions.TryGetValue(_target.Key,out var state))DisplaySession(state);else{SessionText.Text="本次运行 · 暂无采样";FailureText.Text="连续失败 · —";}
        await RefreshAsync();
        if(RouteRawPanel.Visibility==Visibility.Visible&&_displayedRoute?.TargetKey==RouteTarget?.Key&&_displayedRoute is not null)RenderRoute(_displayedRoute);
    }
    private async void Page_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(OverviewPanel is null||RoutePanel is null||EventsPanel is null)return;
        _detailLoadedKey=null;_selectionVersion++;Chart.InvalidatePlot();
        CancelRouteHistoryLoad();
        if(_annotationService?.Mode==1)_annotationService.CancelRequests();
        CancelMetadata();
        OverviewPanel.Visibility=PageBox.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;
        RoutePanel.Visibility=PageBox.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed;
        EventsPanel.Visibility=PageBox.SelectedIndex==2?Visibility.Visible:Visibility.Collapsed;
        DashboardScroll.ChangeView(null,0,null,true);_recordsDirty=true;await RefreshAsync();
    }
    private void Route_Click(object sender,RoutedEventArgs e)
    {
        if(_selectedProfileId is not null&&_manager?.RequestRoute(_selectedProfileId)==true){RouteStatus.Text="路由检查已排队";RouteButton.IsEnabled=false;}
    }
    private async void Legacy_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            if(_cancellation is not null)return;
            _target=Target.Parse(AddressBox.Text,PortBox.Value);_primaryTarget=_target;_secondaryTarget=null;
            _lastSample=null;_recordsDirty=true;LatestValue.Text="—";TargetText.Text=_target.Label+" · 旧版参数未知";
            AvailabilityLabel.Text="旧版 TCP 建连成功率";PageBox.SelectedIndex=0;ExportButton.IsEnabled=true;
            await RefreshAsync();StatusText.Text="旧版 TCP 历史 · 原始参数未记录，不与新配置混算";
        }
        catch(Exception ex){ShowError(ex);}
    }
    private async Task RefreshDetailsAsync()
    {
        if(PageBox.SelectedIndex==1){await RefreshRouteHistoryAsync();return;}
        if(PageBox.SelectedIndex!=2||!_recordsDirty||_target is null||RouteTarget is null)return;
        _recordsDirty=false;int selection=_selectionVersion;var target=_target;var routeTarget=RouteTarget;
        var result=await Task.Run(()=>
        {
            var events=_history.LoadEvents(target,100);
            if(target.Key!=routeTarget.Key)events=events.Concat(_history.LoadEvents(routeTarget,100)).OrderByDescending(e=>e.Time).Take(100).ToList();
            return events;
        });
        if(_hidden||_workspaceView!=1||selection!=_selectionVersion||_target?.Key!=target.Key){_recordsDirty=true;return;}
        if(PageBox.SelectedIndex!=2)return;
        EventRows.Children.Clear();
        if(result.Count==0)EventRows.Children.Add(new TextBlock{Text="当前配置暂无事件。",FontSize=13});
        foreach(var item in result)
        {
            var content=new StackPanel{Spacing=6};
            content.Children.Add(new TextBlock{Text=$"{item.Time.ToLocalTime():MM-dd HH:mm:ss} · {IncidentDetector.Label(item.Kind)}",FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,FontSize=13});
            content.Children.Add(new TextBlock{Text=item.Detail,TextWrapping=TextWrapping.Wrap,FontSize=12});
            if(item.RouteId is not null)
            {
                var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
                var current=new Button{Content="查看当时路由",FontSize=12};current.Click+=async(_,_)=>await ShowRouteAsync(item.RouteId);actions.Children.Add(current);
                if(item.PreviousRouteId is not null){var previous=new Button{Content="对照之前路由",FontSize=12};previous.Click+=async(_,_)=>await ShowRouteAsync(item.PreviousRouteId);actions.Children.Add(previous);}
                content.Children.Add(actions);
            }
            EventRows.Children.Add(new Border{Padding=new Thickness(14),CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1),BorderBrush=(SolidColorBrush)Application.Current.Resources["SubtleBorderBrush"],Child=content});
        }
    }
    private async Task ShowRouteAsync(string id)
    {
        try
        {
            var target=RouteTarget;if(target is null)return;
            var route=await Task.Run(()=>_history.LoadRoute(target,id));
            if(RouteTarget?.Key!=target.Key)return;
            if(route is null){StatusText.Text="关联路由不在当前保留期内。";return;}
            SetWorkspace(1);PageBox.SelectedIndex=1;
            await RefreshAsync();
            if(RouteTarget?.Key!=target.Key||PageBox.SelectedIndex!=1)return;
            if(_routeHistory?.Memberships.TryGetValue(id,out var member)==true)await SelectHistoryObservationAsync(member,false);
            _routeHistoryReading=true;_routeCellRun=id;
            await LoadRawRouteAsync(route.Id);
        }
        catch(Exception ex){ShowError(ex);}
    }
    private void RouteSelection_Changed(object sender,SelectionChangedEventArgs e){if(!_loadingRoutes){if(_annotationService?.Mode==1)_annotationService.CancelRequests();RenderSelectedRoute();}}
    private void RenderSelectedRoute()
    {
        if(RouteRows is null)return;
        if(RouteSelector.SelectedItem is ComboBoxItem{Tag:string id})_=LoadRawRouteAsync(id);
    }
    private void RenderRoute(RouteRun run)
    {
        if(_hidden||_workspaceView!=1||PageBox.SelectedIndex!=1||run.TargetKey!=RouteTarget?.Key){_recordsDirty=true;return;}
        CancelMetadata();_displayedRoute=run;
        var annotations=new Dictionary<string,List<TextBlock>>();
        RouteDetails.Text=$"{run.Started.ToLocalTime():yyyy-MM-dd HH:mm:ss} → {run.Finished.ToLocalTime():HH:mm:ss}\n{run.Outcome} · 超时 {run.TimeoutMs} ms · 最多 {run.MaxHops} 跳\n目标 {run.Address}\n{(string.IsNullOrEmpty(run.ContextDescription)?"此快照未记录出口说明":run.ContextDescription)}";
        if(_routeRawIsReference)RouteDetails.Text="参考来源 · 以下均为来源快照实测数据\n"+RouteDetails.Text;
        if(run.ProbeOptions is RouteOptions policy)
            RouteDetails.Text+=$"\n初测 {policy.Queries} 次 / 间隔 {policy.InitialSpacingMs} ms；无回应跳补测间隔 {policy.SupplementSpacingMs} ms，每跳合计最多 {policy.MaxAttemptsPerHop} 次\n补测最多 {policy.SupplementBudgetSeconds} 秒，计入整轮 {run.BudgetSeconds} 秒预算";
        if(run.SupplementOutcome.Length>0)RouteDetails.Text+="\n"+run.SupplementOutcome;
        RouteRows.Children.Clear();
        foreach(var hop in run.Probes.GroupBy(p=>p.Ttl).OrderBy(g=>g.Key))
        {
            var panel=new StackPanel{Spacing=5};
            panel.Children.Add(new TextBlock{Text=RouteHopDisplay.Title(hop.Key,hop),FontSize=13,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
            foreach(var address in hop.Where(p=>p.Address is not null).GroupBy(p=>p.Address!).OrderBy(g=>g.Key,StringComparer.Ordinal))
            {
                string times=string.Join(" / ",address.Select(p=>(p.IsSupplemental?"补测 ":"")+(p.RttMs is double r?(r<1?"<1 ms":$"{r:F0} ms"):p.Label)));
                panel.Children.Add(new TextBlock{Text=$"{address.Key}    {times}",FontSize=12,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true});
                {
                    var local=NodeMetadataClient.LocalLabel(address.Key);
                    var note=new TextBlock{Text=local??"正在读取节点注释…",FontSize=11,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true,Foreground=(SolidColorBrush)Application.Current.Resources["SecondaryTextBrush"]};
                    panel.Children.Add(note);
                    if(local is null){if(!annotations.TryGetValue(address.Key,out var list))annotations[address.Key]=list=new();list.Add(note);}
                }
            }
            var probeDetails=new Expander{Header="逐次探测时间 / 状态",FontSize=11,HorizontalAlignment=HorizontalAlignment.Stretch,Visibility=RouteProbeTimesBox.IsChecked==true?Visibility.Visible:Visibility.Collapsed};
            probeDetails.Expanding+=(sender,_)=>
            {
                if(sender is not Expander details||details.Content is not null)return;
                details.Content=new TextBlock{FontSize=11,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true,
                    Text=string.Join("\n",hop.OrderBy(p=>p.Sequence).Select(p=>$"#{p.Sequence} · {(p.IsSupplemental?"补测":"初测")} · {(p.SentAt is {} sent?sent.ToLocalTime().ToString("HH:mm:ss.fff"):"发送时间未记录")} · {p.Address??"—"} · {p.Label} · RTT {RouteTableRow.FormatRtt(p.RttMs)} ms"))};
            };
            panel.Children.Add(probeDetails);
            RouteRows.Children.Add(new Border{Padding=new Thickness(12),Background=(SolidColorBrush)Application.Current.Resources["PanelBackgroundBrush"],CornerRadius=new CornerRadius(6),Child=panel});
        }
        if(PageBox.SelectedIndex==1&&_metadataClient is not null)
        {
            _metadataCancellation=new CancellationTokenSource();
            _annotationTask=FillAnnotationsAsync(run,annotations,MetadataBox.IsChecked==true,_metadataCancellation.Token);
        }
    }
    private void RouteProbeTimes_Click(object sender,RoutedEventArgs e)
    {
        foreach(var expander in RouteRows.Children.OfType<Border>().Select(b=>(StackPanel)b.Child).SelectMany(p=>p.Children.OfType<Expander>()))
            expander.Visibility=RouteProbeTimesBox.IsChecked==true?Visibility.Visible:Visibility.Collapsed;
    }
    private async Task VerifyAnnotationsAsync(Action<bool,string> check)
    {
        check(MetadataBox.IsChecked!=true,"existing offline settings remain off after upgrade");
        var now=DateTimeOffset.UtcNow;
        // Synthetic presentation fixture. This test never contacts the lookup provider.
        await Task.Run(()=>_history.SaveNodeMetadata(new NodeMetadata("1.1.1.1",true,13335,"示例运营商（模拟缓存）","示例组织","示例国家","示例地区","示例城市",now,now.AddDays(7),"")));
        PageBox.SelectedIndex=1;await RefreshAsync();
        var run=new RouteRun("annotation-ui-test",_primaryTarget!.Key,_primaryTarget.Address,"test",now,now,"节点注释布局自检","模拟路由与模拟缓存 · 非实测路径",false,1000,32,
            new HopProbe[]{new(1,1,"192.168.1.1",11013,1),new(2,1,"1.1.1.1",11013,20),new(3,1,null,11010,null)}){ContextDescription="本机界面自检 · 不向外部提交测试节点"};
        RenderRoute(run);await _annotationTask;
        var labels=RouteRows.Children.OfType<Border>().Select(b=>(StackPanel)b.Child).SelectMany(p=>p.Children.OfType<TextBlock>()).Select(t=>t.Text).ToArray();
        Services.StartupDiagnostics.Write("Annotation UI fixture: "+string.Join(" | ",labels));
        check(labels.Any(s=>s.Contains("AS13335")&&s.Contains("示例运营商")&&s.Contains("示例地区")&&s.Contains("来源 ipwho.is")),"NativeAOT cache renders ASN/operator/location/source without HTTP");
        check(labels.Contains("私网地址"),"local route nodes receive an offline annotation");
        MetadataBox.IsChecked=true;Metadata_Click(MetadataBox,new RoutedEventArgs());await _annotationTask;
        check(Services.Settings.Load().EnableNodeMetadata,"online annotation preference persists");
        MetadataBox.IsChecked=false;Metadata_Click(MetadataBox,new RoutedEventArgs());await _annotationTask;
        await CaptureForVerificationAsync("-annotations");
        RenderSelectedRoute();PageBox.SelectedIndex=0;
    }
}
