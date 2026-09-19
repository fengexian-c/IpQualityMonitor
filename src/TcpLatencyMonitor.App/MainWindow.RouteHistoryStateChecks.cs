using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TcpLatencyMonitor.Core;

namespace TcpLatencyMonitor.App;

public sealed partial class MainWindow
{
    private async Task VerifyRouteHistoryStateAsync(Action<bool,string> check)
    {
        var original=_settings.Profiles.Single(p=>p.Id==_selectedProfileId);int protocol=ViewProtocolBox.SelectedIndex,period=PeriodBox.SelectedIndex;
        var profile=new TargetProfile{Id="route-state-fixture",Name="路由状态自检",Address="127.0.0.42",Mode="Both",Port=443,TimeoutMilliseconds=_settings.GlobalMonitoring.TimeoutMilliseconds};
        _settings.Profiles.Add(profile);_timer.Stop();
        try
        {
            SelectProfile(profile);ViewProtocolBox.SelectedIndex=0;PeriodBox.SelectedIndex=0;PageBox.SelectedIndex=1;await RefreshAsync();
            var now=DateTimeOffset.UtcNow;var icmp=profile.Primary;var tcp=profile.Secondary!;
            _history.Add(icmp,new(now.AddMinutes(-2),ProbeStatus.Success,10,10,"isolated state fixture"));
            _history.Add(tcp,new(now.AddMinutes(-2),ProbeStatus.Success,200,200,"isolated state fixture"));
            RouteRun Fixture(string id,DateTimeOffset start,bool missing=false)=>new(id,icmp.Key,icmp.Address,"state-fixture",start,start.AddSeconds(8),"界面自检","模拟数据，非真实路由",true,1500,32,
                new[]{"10.0.0.1","8.8.8.8","192.0.2.3","192.0.2.4",icmp.Address}.SelectMany((ip,i)=>Enumerable.Range(1,3).Select(q=>new HopProbe(i+1,q,missing&&i==1?null:ip,missing&&i==1?11010:i==4?0:11013,missing&&i==1?null:10+i){SentAt=start.AddSeconds(i).AddMilliseconds(q*100),IsSupplemental=q==3})).ToArray()){ProbeExecutionId=id,ProbeOptions=new()};
            var run=Fixture("state-route",now.AddMinutes(-1));_history.SaveRoute(run);
            _history.SaveNodeMetadata(new("8.8.8.8",true,15169,"fixture","fixture","美国","fixture","旧城",now,now.AddDays(1),"fixture"));
            await RefreshRouteHistoryAsync(true);await _routeMatrixTask;
            await SelectHistoryObservationAsync(_routeHistory!.Memberships[run.Id],false);await _routeMatrixTask;
            var cutoff=_routeHistoryUntil;var frozen=_routeHistory;
            check(_routeHistoryTrend!.Total.Average==10,"native history R2 fixture isolates 10 ms ICMP statistics");
            ViewProtocolBox.SelectedIndex=1;await RefreshAsync();
            check(_routeHistoryTrend?.Total.Average==200&&_routeTrendTarget==tcp.Key&&_routeHistoryUntil==cutoff&&ReferenceEquals(frozen,_routeHistory),"native history R2 frozen reading changes to TCP 200 ms at the same cutoff");
            var newer=Fixture("state-new",now.AddSeconds(-15));_history.SaveRoute(newer);_recordsDirty=true;await RefreshAsync();
            check(ReferenceEquals(frozen,_routeHistory)&&_routeHistoryUntil==cutoff&&RouteHistoryUpdate.Visibility==Visibility.Visible,"native history new route signals update without moving a frozen snapshot");
            PeriodBox.SelectedIndex=1;await RefreshAsync();
            check(_routeHistoryUntil==cutoff&&ReferenceEquals(frozen,_routeHistory)&&_routeHistoryTrend?.Days==7,"native history 7-day filter keeps the frozen cutoff and branch catalogue");
            await FollowLatestRouteHistoryAsync();await _routeMatrixTask;
            check(!_routeHistoryReading&&_routeRepresentatives.Count==0&&_routeHistoryColumns.Select(Representative).Any(m=>m?.Observation.Id==newer.Id),"native history update-to-latest releases old representatives and resumes following");
            await SelectHistoryObservationAsync(_routeHistory!.Memberships[run.Id],false);await _routeMatrixTask;
            _history.SetCalibration(new("8.8.8.8","美国","fixture","新城","isolated fixture",now,now.AddDays(1)));
            await RefreshGeoEvidenceAsync();
            check(_routeHistoryMetadata.GetValueOrDefault("8.8.8.8")?.Calibration?.City=="新城"&&_routeMatrixRows.SelectMany(r=>r.Cells).Any(c=>c.Text.Text.Contains("新城")),"native history R4 calibration refreshes a frozen visible matrix");
            await LoadRawRouteAsync(run.Id);await _annotationTask;RouteRawToggle_Click(RouteRawPanel,new RoutedEventArgs());
            await RefreshGeoEvidenceAsync();
            check(RouteRawPanel.Visibility==Visibility.Collapsed&&_displayedRoute?.Id==run.Id,"native history R5 annotation refresh preserves a closed raw panel");
            var source=Fixture("state-source",now.AddHours(-24).AddMinutes(-5));var missing=Fixture("state-missing",now.AddHours(-24).AddMinutes(5),true);
            _history.SaveRoute(source);_history.SaveRoute(missing);_routeHistoryReading=false;PeriodBox.SelectedIndex=0;await RefreshAsync();await RefreshRouteHistoryAsync(true);
            await SelectHistoryObservationAsync(_routeHistory!.Memberships[missing.Id],false);await _routeMatrixTask;
            var cell=_routeMatrixRows.SelectMany(r=>r.Cells).First(c=>c.Button.Tag is ValueTuple<RouteMembership,int> pair&&pair.Item1.Observation.Id==missing.Id&&pair.Item2==2);
            RouteCell_Click(cell.Button,new RoutedEventArgs());cutoff=_routeHistoryUntil;
            check(_routeReferenceRun==source.Id&&RouteCellReferenceOriginal.IsEnabled&&!_routeHistoryWindow.Any(m=>m.Observation.Id==source.Id),"native history source action cites retained evidence outside the 24-hour filter");
            await LoadRawRouteAsync(source.Id,true);await _annotationTask;
            check(_displayedRoute?.Id==source.Id&&RouteDetails.Text.Contains("参考来源")&&_routeHistoryUntil==cutoff,"native history opening reference original keeps the current time filter");
            RouteProbeTimesBox.IsChecked=true;RouteProbeTimes_Click(RouteProbeTimesBox,new RoutedEventArgs());
            var expander=RouteRows.Children.OfType<Border>().Select(b=>(StackPanel)b.Child).SelectMany(p=>p.Children.OfType<Expander>()).First();expander.ApplyTemplate();expander.IsExpanded=true;await Task.Delay(60);
            string sentAt=source.Probes[0].SentAt!.Value.ToLocalTime().ToString("HH:mm:ss.fff");
            check(expander.Content is TextBlock detail&&detail.Text.Contains("初测")&&detail.Text.Contains("补测")&&detail.Text.Contains(sentAt),"native history original probe details retain SentAt and initial/supplement identity");
            profile.PreviousMeasurements.Add(new("127.0.0.43",443,"Both",999,now.AddDays(-1)));RebuildRevisions();RevisionBox.SelectedIndex=1;await RefreshAsync();
            RouteRawToggle_Click(RouteRawPanel,new RoutedEventArgs());
            check(_routeHistoryWindow.Count==0&&_displayedRoute is null&&RouteRows.Children.Count==0&&RouteDetails.Text.Length==0&&RouteRawPanel.Visibility==Visibility.Collapsed,"native history R3 empty revision clears every previous raw field");
            RevisionBox.SelectedIndex=0;await RefreshAsync();var pending=LoadRawRouteAsync(source.Id);RevisionBox.SelectedIndex=1;await pending;await RefreshAsync();
            check(_displayedRoute is null&&RouteRows.Children.Count==0&&!RouteCellOriginal.IsEnabled,"native history stale raw completion cannot repopulate another revision");
            for(int i=0;i<8;i++){RevisionBox.SelectedIndex=i%2;ViewProtocolBox.SelectedIndex=i%2;PeriodBox.SelectedIndex=i%2;}
            RevisionBox.SelectedIndex=0;ViewProtocolBox.SelectedIndex=0;PeriodBox.SelectedIndex=0;await RefreshAsync();await _routeMatrixTask;
            check(RouteTarget?.Key==icmp.Key&&_routeTrendTarget==icmp.Key&&_routeHistoryTrend?.Days==1&&_routeHistoryTarget==icmp.Key,"native history rapid revision/protocol/period changes apply only the final identity");
            _routeHistoryReading=false;RouteHistoryDisplay_Click(RouteHistoryAllNodes,new RoutedEventArgs());
            check(!_routeHistoryReading,"native history presentation toggles do not freeze following mode");
            _history.SaveNodeMetadata(new("1.1.1.1",true,13335,"second fixture","second fixture","美国","fixture","第二城",now,now.AddDays(1),"fixture"));
            var multiple=Fixture("state-multiple",DateTimeOffset.UtcNow.AddSeconds(-10));
            multiple=multiple with{Probes=multiple.Probes.Append(new HopProbe(2,4,"1.1.1.1",11013,25){IsSupplemental=true,SentAt=multiple.Started.AddSeconds(4)}).ToArray()};
            _history.SaveRoute(multiple);await RefreshRouteHistoryAsync(true);await SelectHistoryObservationAsync(_routeHistory!.Memberships[multiple.Id],false);await _routeMatrixTask;
            string multiText=RouteMatrixCell(_routeHistory.Memberships[multiple.Id],2);
            check(multiText.Contains("8.8.8.8")&&multiText.Contains("新城")&&multiText.Contains("1.1.1.1")&&multiText.Contains("第二城"),"native history multiple IPs retain their own city and network annotations");
            check(_routeColumnPickers.Where((_,i)=>_routeHistoryColumns[i] is not null).All(p=>p.SelectedItem is ComboBoxItem item&&item.Content is string caption&&caption.Length>0&&Equals(p.SelectionBoxItem,caption)),"native history reused path picker displays its selected caption after revision switches");
            await CaptureForVerificationAsync("-route-history-state");
        }
        finally
        {
            RouteProbeTimesBox.IsChecked=false;_settings.Profiles.Remove(profile);SelectProfile(original);ViewProtocolBox.SelectedIndex=protocol;PeriodBox.SelectedIndex=period;PageBox.SelectedIndex=0;
            _settings.Save();await RefreshAsync();_timer.Start();
        }
    }
}
