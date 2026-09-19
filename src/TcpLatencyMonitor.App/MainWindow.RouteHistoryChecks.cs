using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TcpLatencyMonitor.Core;

namespace TcpLatencyMonitor.App;

public sealed partial class MainWindow
{
    private async Task VerifyRouteHistoryUiAsync(Action<bool,string> check)
    {
        var target=_primaryTarget!;var now=DateTimeOffset.UtcNow;
        RouteRun Fixture(string id,int minute,string? differing,int missing=0)
        {
            string[] ips=["10.0.0.1","192.0.2.2","192.0.2.3",differing??"","192.0.2.5",target.Address];
            return new(id,target.Key,target.Address,"route-ui",now.AddMinutes(minute),now.AddMinutes(minute).AddSeconds(8),"路由历史界面自检","模拟数据，非真实路径",true,1500,32,
                ips.SelectMany((ip,i)=>Enumerable.Range(1,ip==""||missing==i+1?10:3).Select(q=>new HopProbe(i+1,q,ip==""||missing==i+1?null:ip,ip==""||missing==i+1?11010:i==5?0:11013,ip==""||missing==i+1?null:10+i){IsSupplemental=q>3})).ToArray())
                {ProbeOptions=new(),ProbeExecutionId=id};
        }
        var a=Fixture("ui-history-a",-60,"198.51.100.8");var b=Fixture("ui-history-b",-50,"198.51.100.9");var u=Fixture("ui-history-u",-48,null);
        foreach(var run in new[]{a,b,u})_history.SaveRoute(run);
        PageBox.SelectedIndex=1;await RefreshAsync();
        _routeHistory=RouteHistoryIndex.Build(new[]{a,b,u}.Select(RouteObservation.From));_routeHistoryUntil=now;_routeHistoryMetadata=new();
        _routeRepresentatives.Clear();Array.Clear(_routeHistoryColumns);_routePatternNames.Clear();RouteRawPanel.Visibility=Visibility.Collapsed;
        RouteHistoryReferences.IsChecked=true;RouteHistoryAllNodes.IsChecked=false;RenderRouteHistory();await _routeMatrixTask;
        var closedChoices=RouteSelector.Items.Cast<object>().ToArray();
        for(int i=0;i<3;i++)RenderRouteHistory();await _routeMatrixTask;
        check(RouteSelector.Items.Count==closedChoices.Length&&closedChoices.Select((item,i)=>ReferenceEquals(item,RouteSelector.Items[i])).All(equal=>equal),"native history closed raw selector does not rebuild its visual items");
        check(_routeHistory.Patterns.Count==2&&_routeColumnPickers.Count==3,"native history matrix shows path alternatives with a bounded column pool");
        check(_routeColumnSummaries.Any(t=>t.Text.Contains("时间推定")),"native history identifies the latest ambiguous snapshot as temporally assigned");
        check(_routeMatrixRows.SelectMany(r=>r.Cells).Any(c=>c.Text.Text.Contains("回应 0/10")&&c.Text.Text.Contains("历史参考")),"native history keeps timeout counts beside referenced IPs");
        check(_routeMatrixRows.Any(r=>r.Cells.Any(c=>c.Text.Text.Contains("共同非公网节点"))),"native history folds shared private rows across all columns");
        check(_routeMatrixRows.Any(r=>r.Ttl.Text=="6"&&r.Ttl.Visibility==Visibility.Visible),"native history never hides the destination with private-node filtering");
        var texts=_routeMatrixRows.SelectMany(r=>r.Cells).Select(c=>c.Text).ToArray();
        RouteHistoryReferences.IsChecked=false;RenderRouteMatrix();
        check(!_routeMatrixRows.SelectMany(r=>r.Cells).Any(c=>c.Text.Text.Contains("历史参考"))&&_routeMatrixRows.SelectMany(r=>r.Cells).Any(c=>c.Text.Text.Contains("回应 0/10")),"native history reference toggle restores original unknown nodes");
        RouteHistoryReferences.IsChecked=true;RouteHistoryAllNodes.IsChecked=true;RenderRouteMatrix();
        check(_routeMatrixRows[0].Cells.Any(c=>c.Text.Text.Contains("10.0.0.1")),"native history all-nodes switch reveals private IPs");
        check(texts.All(t=>_routeMatrixRows.SelectMany(r=>r.Cells).Any(c=>ReferenceEquals(c.Text,t))),"native history reuses matrix text and button instances across filtering");
        for(int i=0;i<250;i++){RouteHistoryReferences.IsChecked=i%2==0;RenderRouteMatrix();}
        check(_routeMatrixRows.Count<=64&&RouteHistoryGrid.Children.Count<=520,"native history repeated toggling keeps a bounded visual tree");
        RouteHistoryReferences.IsChecked=true;RouteHistoryAllNodes.IsChecked=false;RenderRouteMatrix();
        var member=_routeHistory.Memberships[u.Id];
        await SelectHistoryObservationAsync(member,false);
        check(_routeRepresentatives[member.PatternId!]==u.Id&&_routeCellRun==u.Id,"native history time selection targets a precise original observation");
        var before=_routeHistory;int previousDays=_timelineDays;_timelineDays=7;RenderRouteHistory();
        check(ReferenceEquals(before,_routeHistory)&&_routeHistory.Memberships[u.Id].PatternId==member.PatternId,"native history range switching filters the existing projection without regrouping");
        _timelineDays=previousDays;RenderRouteHistory();
        await CaptureForVerificationAsync("-route-history");
        await LoadRawRouteAsync(u.Id);await _annotationTask;
        check(_displayedRoute?.Id==u.Id&&RouteRawPanel.Visibility==Visibility.Visible,"native history opens the exact untouched raw snapshot");
        var choices=RouteSelector.Items.Cast<object>().ToArray();RenderRouteRecordSelector();RenderRouteRecordSelector();
        check(choices.Length>0&&choices.Select((item,i)=>ReferenceEquals(item,RouteSelector.Items[i])).All(equal=>equal),"native history open raw selector reuses its bounded item pool");
        RouteRawPanel.Visibility=Visibility.Collapsed;_routeHistoryReading=false;_routeHistory=null;_routeHistoryStamp=null;_recordsDirty=true;
        PageBox.SelectedIndex=0;await RefreshAsync();
    }
}
