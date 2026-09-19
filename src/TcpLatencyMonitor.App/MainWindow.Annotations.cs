using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TcpLatencyMonitor.Core;

namespace TcpLatencyMonitor.App;
public sealed partial class MainWindow
{
    private RouteAnnotationService? _annotationService;
    private bool _overviewRouteDirty=true;private string? _overviewTargetKey,_lastViewAttemptId,_latestRouteId;
    private void InitializeAnnotationService()
    {
        _annotationService=new RouteAnnotationService(_history,_metadataClient!);
        _annotationService.Mode=!_settings.EnableNodeMetadata||!_geoConfigurationValid?0:_settings.BackgroundNodeMetadata?2:1;
        SetOverviewTable(null,null);
        _annotationService.Saved+=saved=>DispatcherQueue.TryEnqueue(()=>{_overviewRouteDirty=true;_recordsDirty=true;_multiLoaded=DateTimeOffset.MinValue;if(!_hidden)_=RefreshAsync();});
        _annotationService.Failed+=message=>Services.StartupDiagnostics.Write("Optional route annotation: "+message);
        _annotationService.Refreshed+=()=>DispatcherQueue.TryEnqueue(()=>{if(!_stopping)_=RefreshGeoEvidenceAsync();});
    }
    private async void OverviewRoute_Click(object sender,RoutedEventArgs e)
    {
        PageBox.SelectedIndex=1;await RefreshAsync();
        if(_latestRouteId is not null)await ShowRouteAsync(_latestRouteId);
    }
    private async Task RefreshOverviewRouteAsync()
    {
        int selection=_selectionVersion;var target=RouteTarget;if(target is null||_hidden||_workspaceView!=1)return;
        if(!_overviewRouteDirty&&_overviewTargetKey==target.Key)return;
        _overviewRouteDirty=false;_overviewTargetKey=target.Key;
        var result=await Task.Run(()=>
        {
            var route=_history.LoadRoutes(target,1).FirstOrDefault();if(route is null)return ((RouteRun?)null,(RouteAnnotation?)null);
            return (route,_history.CurrentRouteAnnotation(route,DateTimeOffset.UtcNow));
        });
        if(_hidden||_workspaceView!=1||selection!=_selectionVersion||RouteTarget?.Key!=target.Key){_overviewRouteDirty=true;return;}
        var (run,annotation)=result;_latestRouteId=run?.Id;
        if(run is null||annotation is null)
        {
            OverviewRouteTitle.Text="最新路由 · 暂无记录";OverviewRouteSummary.Text=_settings.EnableRoutes?"启动监控后会检查路由。":"路由检查已关闭，可在统一设置中启用。";
            SetOverviewTable(null,null);return;
        }
        OverviewRouteTitle.Text=$"最新路由 · {run.Started.ToLocalTime():MM-dd HH:mm} · {(run.Reached?"到达目标":"部分路径")}";
        OverviewRouteSummary.Text=annotation.Summary;
        SetOverviewTable(run,annotation);
    }
    private RouteAnnotation PreviewAnnotation(RouteRun run)
    {
        var metadata=new Dictionary<string,NodeMetadata>();
        foreach(var ip in run.Probes.Where(p=>p.Address is not null).Select(p=>p.Address!).Distinct())
            if(_history.LoadNodeMetadata(ip) is NodeMetadata data)metadata[ip]=data;
        return RouteClassifier.Classify(run,metadata,DateTimeOffset.UtcNow,"当前缓存预览；无当时注释快照");
    }
    private async Task FillAnnotationsAsync(RouteRun route,Dictionary<string,List<TextBlock>> labels,bool online,CancellationToken token)
    {
        int selection=_selectionVersion;
        bool Current()=>!token.IsCancellationRequested&&selection==_selectionVersion&&_displayedRoute?.Id==route.Id&&RouteTarget?.Key==route.TargetKey&&_workspaceView==1&&PageBox.SelectedIndex==1&&!_hidden;
        try
        {
            bool original=OriginalAnnotationBox.IsChecked==true;
            var result=await Task.Run(()=>original?_history.LoadRouteAnnotation(route.Id,true):_history.CurrentRouteAnnotation(route,DateTimeOffset.UtcNow),token);
            if(result is null)result=await Task.Run(()=>PreviewAnnotation(route),token);
            if(!Current())return;
            ApplyAnnotation(result,labels);
            if(online&&!original&&_lastViewAttemptId!=route.Id&&result.Nodes.Any(n=>NodeMetadataClient.LocalLabel(n.Address) is null&&(n.Metadata?.Source!=_metadataClient!.Options.Primary||!NodeMetadataClient.IsFresh(n.Metadata,_metadataClient.Options,DateTimeOffset.UtcNow)))&&
                !result.Origin.Contains("重新解释")&&!result.Origin.Contains("无当时")&&_annotationService is not null)
            {
                _lastViewAttemptId=route.Id;
                RouteAnnotationEvidence.Text+=" · 正在补全（每轮最多 20 秒）";
                var updated=await _annotationService.Request(route,true);
                if(Current()&&updated is not null)ApplyAnnotation(updated,labels);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex)
        {
            if(!Current())return;
            Services.StartupDiagnostics.Write("Route annotation view failed.",ex);RouteAnnotationEvidence.Text="注释暂不可用，探测不受影响。";
            foreach(var block in labels.Values.SelectMany(v=>v))
                if(block.Text=="正在读取节点注释…")block.Text="节点注释读取失败；可点击“补全注释”重试。";
        }
    }
    private void ApplyAnnotation(RouteAnnotation result,Dictionary<string,List<TextBlock>> labels)
    {
        RouteAnnotationSummary.Text=result.Summary;
        RouteAnnotationEvidence.Text=$"{result.Origin} · 保存/解释 {result.Time.ToLocalTime():MM-dd HH:mm:ss} · 规则 {result.RuleVersion}\n"+
            (result.InterpretationVersion.Length==0?"旧版未记录分类计数\n":$"ASN 未查得 {result.MissingAsn} 个 · 线路未识别 {result.MissingMetadata} 个 · 归属待核对 {result.Conflicts} 个\n")+
            "按 TTL 排序，地区为 IP 定位；非 BGP AS_PATH，不认证 GIA/GT。";
        foreach(var (ip,blocks) in labels)
        {
            var node=result.Nodes.FirstOrDefault(n=>n.Address==CidrBlock.Normalize(ip));
            string text=node is null?"归属未知":node.Name+" · "+node.Evidence;
            if(node?.Identity?.Prefix is PrefixRule prefix)
                text+=$"\n地址段依据：{prefix.Cidr} · {prefix.RegistryName} · 核对 {prefix.Verified:yyyy-MM-dd}\n来源：{prefix.Source}";
            if(node?.Metadata is NodeMetadata data)
            {
                text+="\n"+data.Description;
                if(data.Success)text+=$"\n来源 {data.Source} · 查询 {data.Queried.ToLocalTime():MM-dd HH:mm} · 缓存有效至 {data.Expires.ToLocalTime():MM-dd HH:mm}";
                if(node.Identity?.Location is {} location)text+=$"\n采用地区：{location.Label} · {location.Marker}\n{location.Details}";
                if(data.AsnSource.Length>0)text+=$"\nASN 来源：{data.AsnSource}";
            }
            else if(node is not null&&NodeMetadataClient.LocalLabel(ip) is null)text+="\nASN：未查得 · 地区未知；可开启在线注释或点击补全";
            foreach(var block in blocks)block.Text=text;
        }
    }
    private void OriginalAnnotation_Click(object sender,RoutedEventArgs e){if(_displayedRoute is not null)RenderRoute(_displayedRoute);}
    private async void Reannotate_Click(object sender,RoutedEventArgs e)
    {
        if(_displayedRoute is null||_annotationService is null)return;
        var route=_displayedRoute;OriginalAnnotationBox.IsChecked=false;
        RouteAnnotationEvidence.Text="正在补全注释；查询有超时上限，探测继续运行。";
        await _annotationService.Request(route,true);
        if(RouteRawPanel.Visibility==Visibility.Visible&&_displayedRoute?.Id==route.Id)RenderRoute(route);
    }
}
