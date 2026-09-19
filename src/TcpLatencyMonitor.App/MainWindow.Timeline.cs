using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TcpLatencyMonitor.Core;

namespace TcpLatencyMonitor.App;
public sealed partial class MainWindow
{
    private Controls.HourMatrix? _hourMatrix;
    private Timeline? _timeline;private string? _timelineKey;private DateTimeOffset _timelineLoaded;
    private int _timelineDays=1;
    private async void Period_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(PeriodBox is null||Chart is null)return;
        _timelineDays=PeriodBox.SelectedIndex==1?7:1;_timelineKey=null;_timeline=null;_multiLoaded=DateTimeOffset.MinValue;_detailLoadedKey=null;Chart.ClearSelection();
        _recordsDirty=true;CancelRouteHistoryLoad();
        HourDetail.Text="点击一个小时，可在折线图中定位对应时段。";
        if(_initialized){_settings.TimelineDays=_timelineDays;try{_settings.Save();}catch(Exception ex){ShowError(ex);}}
        await RefreshAsync();
    }
    private void HeatMetric_Changed(object sender,SelectionChangedEventArgs e){if(_timeline is not null)RenderHours(_timeline);}
    private async Task RefreshTimelineAsync(Target target)
    {
        int selection=_selectionVersion;int days=_timelineDays;string key=target.Key+"|"+days;var now=DateTimeOffset.UtcNow;
        if(_timelineKey==key&&now-_timelineLoaded<TimeSpan.FromSeconds(15))return;
        var result=await Task.Run(()=>_history.LoadTimeline(target,now,days));
        if(_hidden||_workspaceView!=1||selection!=_selectionVersion||_target?.Key!=target.Key||_timelineDays!=days)return;
        _timeline=result;_timelineKey=key;_timelineLoaded=now;
        Chart.SetData(result.Points,result.StepMinutes,result.Until);
        ChartFrom.Text=result.From.ToLocalTime().ToString("MM-dd HH:mm");ChartUntil.Text=result.Until.ToLocalTime().ToString("MM-dd HH:mm");
        TimelineInfo.Text=$"每点 {result.StepMinutes} 分钟 · 平均 {Milliseconds(result.Total.Average)} · 失败 {result.Total.Attempts-result.Total.Successes}/{result.Total.Attempts} · 截至 {result.Until.ToLocalTime():HH:mm:ss}";
        RenderHours(result);
    }
    private void RenderHours(Timeline timeline)
    {
        if(_hidden||_workspaceView!=1)return;
        var averages=timeline.Hours.Where(b=>b.Average.HasValue).Select(b=>b.Average!.Value).ToArray();
        double min=averages.Length==0?0:averages.Min(),max=averages.Length==0?0:averages.Max();bool loss=HeatMetricBox.SelectedIndex==1;
        _hourMatrix??=new(HourGrid,(from,until,description)=>{Chart.SelectInterval(from,until);HourDetail.Text=description.Replace('\n',' ');});
        _hourMatrix.Update(timeline,loss,min,max);
        HeatLegend.Text=(loss?"青→橙：失败比例 0%→100%":$"青→橙：小时均值 {min:F1}→{max:F1} ms（当前窗口）")+" · × 全部失败 · 点号含失败 · ! 仅本地错误 · 灰未采样 · 空位范围外。颜色仅代表已有样本。";
    }
    private static SolidColorBrush HeatBrush(double ratio)
    {
        ratio=Math.Clamp(ratio,0,1);return Brush((byte)(31+ratio*205),(byte)(158-ratio*73),(byte)(149-ratio*95));
    }
    private async Task VerifyOverviewFeaturesAsync(Action<bool,string> check)
    {
        PageBox.SelectedIndex=0;PeriodBox.SelectedIndex=0;_timelineKey=null;await RefreshAsync();
        check(_timeline is {Days:1,StepMinutes:5}&&_timeline.Points.Count==289,"24-hour selector controls shared chart and heatmap data");
        PeriodBox.SelectedIndex=1;await RefreshAsync();
        check(_timeline is {Days:7,StepMinutes:30}&&_timeline.Points.Count==337&&HourGrid.RowDefinitions.Count>=8&&HourGrid.ColumnDefinitions.Count==25,"seven-day selector renders date/hour matrix and matching chart");
        HeatMetricBox.SelectedIndex=1;check(HeatLegend.Text.Contains("失败比例"),"hour cells switch between latency and failure rate");HeatMetricBox.SelectedIndex=0;
        var now=DateTimeOffset.UtcNow;var target=_primaryTarget!;
        var run=new RouteRun(Guid.NewGuid().ToString("N"),target.Key,target.Address,"fixture",now,now,"模拟路由布局自检","模拟路径（非实测）",true,500,32,
            new HopProbe[]{new(1,1,"192.168.1.1",11013,1),new(2,1,"1.1.1.1",11013,20),new(3,1,"59.43.250.50",11013,38),new(3,2,"59.43.250.50",11013,40),new(3,3,null,11010,null),new(4,1,"59.43.246.178",11013,161),new(5,1,null,11010,null),new(6,1,target.Address,0,42)}){ContextDescription="仅用于界面自检的模拟路径与节点归属；城市为模拟值，非实测位置"};
        await Task.Run(()=>
        {
            _history.SaveRoute(run);
            _history.SaveNodeMetadata(new("1.1.1.1",true,4837,"模拟运营商","模拟组织","中国","北京市","北京",now,now.AddDays(7),""));
            _history.SaveNodeMetadata(new("59.43.250.50",true,null,"模拟运营商","模拟组织","中国","上海市","上海",now,now.AddDays(7),""));
            _history.SaveNodeMetadata(new("59.43.246.178",true,null,"模拟运营商","模拟组织","中国","上海市","上海",now,now.AddDays(7),""));
        });
        await _annotationService!.Request(run,false);_overviewRouteDirty=true;await RefreshAsync();
        check(OverviewRouteSummary.Text.Contains("CN2")&&OverviewRouteSummary.Text.Contains("4837"),"overview displays classified route summary without opening history");
        string TableText()=>string.Join("|",OverviewRouteGrid.Children.Select(e=>e is TextBlock t?t.Text:e is Button{Content:TextBlock b}?b.Text:""));
        check(OverviewRouteSummary.Text.Contains("地址段归属")&&TableText().Contains("IP 定位")&&TableText().Contains("上海")&&TableText().Contains("北京")&&OverviewRouteGrid.ColumnDefinitions.Count==5,"overview aligns IP, city, network and RTT in five columns");
        check(_overviewTableModel is {HiddenNonPublic:1,HiddenNoReply:1}&&_overviewTableModel.Rows.Count==3&&_overviewTableModel.Rows[^1].Target,"overview hides private and unresponsive rows while retaining a private target");
        var merged=_overviewTableModel!.Rows.Single(r=>r.Merged);
        check(merged.Hops=="3–4"&&TableText().Contains("39～161"),"native overview displays merged hop range and range of per-hop RTT means");
        OverviewRouteTitle.Text+=" · 模拟路由";StatusText.Text="界面自检 · 历史曲线与路由含模拟数据，不进入发布包";
        await CaptureForVerificationAsync("-week");
        ToggleOverviewGroup(merged);
        check(TableText().Contains("59.43.250.50")&&TableText().Contains("59.43.246.178")&&TableText().Contains("39"),"expanding a merged row reveals both IPs and their individual RTTs");
        await CaptureForVerificationAsync("-table-expanded");ToggleOverviewGroup(merged);
        OverviewAllNodes.IsChecked=true;OverviewNodes_Click(OverviewAllNodes,new RoutedEventArgs());
        check(TableText().Contains("私网地址")&&TableText().Contains("未回应")&&_overviewTableModel!.HiddenNonPublic==0&&_overviewTableModel.HiddenNoReply==0,"show-all restores filtered nodes in the overview table");
        await CaptureForVerificationAsync("-table-all");OverviewAllNodes.IsChecked=false;OverviewNodes_Click(OverviewAllNodes,new RoutedEventArgs());
        var longRun=run with{Id="long-overview-fixture",Probes=Enumerable.Range(1,12).Select(i=>new HopProbe(i,1,i==1?"10.0.0.1":i==2?null:i==12?target.Address:$"1.2.3.{i}",i==2?11010:i==12?0:11013,i==2?null:i)).ToArray()};
        SetOverviewTable(longRun,RouteClassifier.Classify(longRun,new Dictionary<string,NodeMetadata>(),now,"fixture"));
        check(OverviewRouteGrid.RowDefinitions.Count==11&&TableText().Contains("1.2.3.11")&&TableText().Contains(target.Address),"overview renders all ten filtered rows with no six-row cutoff");
        await CaptureForVerificationAsync("-table-long");
        OverviewAllNodes.IsChecked=true;OverviewNodes_Click(OverviewAllNodes,new RoutedEventArgs());
        check(OverviewRouteGrid.RowDefinitions.Count==13&&TableText().Contains("10.0.0.1")&&TableText().Contains("未回应")&&TableText().Contains(target.Address),"single show-all switch exposes all twelve nodes without an additional expand step");
        await CaptureForVerificationAsync("-table-long-all");
        OverviewAllNodes.IsChecked=false;
        SetOverviewTable(run,_history.LoadRouteAnnotation(run.Id));
        await ShowRouteAsync(run.Id);await _annotationTask;
        var text=string.Join("\n",RouteRows.Children.OfType<Border>().Select(b=>(StackPanel)b.Child).SelectMany(p=>p.Children.OfType<TextBlock>()).Select(t=>t.Text));
        check(text.Contains("CN2-BB")&&text.Contains("59.43.0.0/16")&&text.Contains("ASN：未知")&&text.Contains("上海"),"NativeAOT details retain registry evidence, missing ASN and city");
        await Task.Delay(100);DashboardScroll.ChangeView(null,400,null,true);
        await CaptureForVerificationAsync("-prefix");DashboardScroll.ChangeView(null,0,null,true);
        var legacyRun=run with{Id=Guid.NewGuid().ToString("N"),Started=now.AddMinutes(-5),Finished=now.AddMinutes(-5)};
        var legacy=_history.LoadRouteAnnotation(run.Id)! with{Id=Guid.NewGuid().ToString("N"),RouteId=legacyRun.Id,Time=now.AddMinutes(-5),
            Summary="旧版注释示例",RuleVersion="2026-09-09.1",InterpretationVersion="",EvidenceKey="",
            Nodes=_history.LoadRouteAnnotation(run.Id)!.Nodes.Select(n=>n with{Identity=null}).ToList()};
        _history.SaveRoute(legacyRun);_history.SaveRouteAnnotation(legacy);
        await ShowRouteAsync(legacyRun.Id);await _annotationTask;
        check(RouteAnnotationSummary.Text.Contains("CN2")&&RouteAnnotationEvidence.Text.Contains("重新解释")&&_history.LoadRouteAnnotation(legacyRun.Id,true)!.Id==legacy.Id,"NativeAOT reinterprets a legacy snapshot offline and retains its first annotation");
        OriginalAnnotationBox.IsChecked=true;RenderRoute(legacyRun);await _annotationTask;
        check(RouteAnnotationSummary.Text=="旧版注释示例"&&RouteAnnotationEvidence.Text.Contains("旧版未记录分类计数"),"earliest legacy view preserves its content without invented new counters");
        OriginalAnnotationBox.IsChecked=false;PageBox.SelectedIndex=0;await RefreshAsync();
        var hour=_timeline!.Hours.First();Chart.SelectInterval(hour.Start,hour.Start.AddHours(1));
        check(_timeline.Points.Sum(b=>b.Attempts)==_timeline.Hours.Sum(b=>b.Attempts),"rendered graph and heatmap retain matching sample counts");
        PeriodBox.SelectedIndex=0;await RefreshAsync();
        if(!OverviewRouteTitle.Text.Contains("模拟路由"))OverviewRouteTitle.Text+=" · 模拟路由";
    }
}
