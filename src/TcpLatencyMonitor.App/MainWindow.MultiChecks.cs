using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TcpLatencyMonitor.App.Services;
using TcpLatencyMonitor.Core;

namespace TcpLatencyMonitor.App;

public sealed partial class MainWindow
{
    private async Task VerifyMultiTargetUiAsync(Action<bool,string> check)
    {
        if(_manager is null||_settings.Profiles.Any(p=>!System.Net.IPAddress.IsLoopback(System.Net.IPAddress.Parse(p.Address))))throw new InvalidOperationException("Multi-target UI verification only supports loopback fixtures.");
        await _manager.StopAllAsync();
        var a=_settings.Profiles.Single().Copy();a.Name="本机服务 A";
        using var server=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);server.Start();using var stopServer=new CancellationTokenSource();
        var serving=Task.Run(async()=>{try{while(true){using var socket=await server.AcceptSocketAsync(stopServer.Token);}}catch(OperationCanceledException){}});
        var b=a.Copy();b.Id=Guid.NewGuid().ToString("N");b.Name="本机服务 B";b.Port=((System.Net.IPEndPoint)server.LocalEndpoint).Port;
        var c=new TargetProfile{Name="另一个本机 IP",Address="127.0.0.2",IntervalSeconds=1,TimeoutMilliseconds=500,EnableRoutes=false};
        var d=new TargetProfile{Name="IPv6 · 暂无采样",Address="::1",IntervalSeconds=1,TimeoutMilliseconds=500,EnableRoutes=false};
        _settings.Profiles=new(){a,b,c,d};_settings.Save();
        var restored=Settings.Load();
        check(restored.Version==4&&restored.Profiles.Count==4&&restored.Profiles[1].Port==b.Port,"NativeAOT serializes and restores independent multi-target profiles");
        check(!restored.EnableNodeMetadata&&!restored.BackgroundNodeMetadata&&File.Exists(AppPaths.SettingsPath+".before-multi.bak"),"migration retains annotation opt-outs and backs up the previous configuration");
        await _manager.StartAsync(a);await _manager.StartAsync(b);await _manager.StartAsync(c);await Task.Delay(2600);
        check(_manager.RunningProfiles==3&&_manager.State(a.Secondary!)?.Success>=2&&_manager.State(b.Secondary!)?.Success>=2,"native ICMP and distinct TCP services run under multiple profiles");
        SelectProfile(a);RebuildTargetList();UpdateLiveStates();
        check(_manager.RunningProfiles==3&&_target?.Key==a.Primary.Key,"selecting a target does not restart or stop monitoring");
        await StopAsync();long before=_manager.State(b.Secondary!)!.Success;await Task.Delay(1300);
        check(!_manager.IsRunning(a.Id)&&_manager.IsRunning(b.Id)&&_manager.State(b.Secondary!)!.Success>before,"native pause stops only the selected profile and keeps a shared ICMP owner alive");
        SelectProfile(b);ViewProtocolBox.SelectedIndex=1;await RefreshAsync();
        check(_target?.Key==b.Secondary!.Key&&TargetText.Text.Contains(b.Port.ToString()),"selected TCP statistics use the selected profile's port");
        await _manager.StopAllAsync();UpdateLiveStates();
        var now=DateTimeOffset.UtcNow;
        await _history.RecordAsync(()=>
        {
            foreach(var target in new[]{a.Primary,a.Secondary!,b.Secondary!,c.Primary})
            {
                int offset=target.Key==b.Secondary!.Key?90:target.Address==c.Address?45:20;
                for(int i=0;i<7*24*4;i++)
                {
                    var time=now.AddMinutes(-i*15);if(time.ToLocalTime().Hour==3)continue;
                    bool peak=time.ToLocalTime().Hour is >=19 and <=22;bool failed=peak&&i%5==0;
                    double latency=offset+(peak?80:0)+Math.Sin(i*.17)*10;
                    _history.Add(target,new(time,failed?ProbeStatus.Timeout:ProbeStatus.Success,failed?null:latency,failed?500:latency,"模拟时段样本，仅用于界面自检"));
                }
            }
        });
        _compared.Clear();foreach(var profile in _settings.Profiles)_compared.Add(profile.Id);
        SetWorkspace(0);GlobalProtocolBox.SelectedIndex=0;PeriodBox.SelectedIndex=0;_multiLoaded=DateTimeOffset.MinValue;await RefreshAsync();
        check(AllTargetsGrid.RowDefinitions.Count==5&&_rowActions.Count==4,"native overview displays four targets with independent actions");
        check(_multiOverview!.Targets.Single(t=>t.ProfileId==d.Id).Timeline!.Total.Attempts==0,"an unstarted IPv6 target remains empty in the overview");
        StatusText.Text="多目标界面自检 · 本机连接和模拟历史，不进入发布包";await CaptureForVerificationAsync("-multi-overview");
        SetWorkspace(2);RenderComparison();
        check(CompareChart.Series.Count==4&&CompareChart.Series.Select(s=>s.Timeline.Until).Distinct().Count()==1,"comparison shares its cutoff across four selected targets");
        PeriodBox.SelectedIndex=1;await RefreshAsync();
        check(_multiOverview.Days==7&&CompareChart.Series.All(s=>s.Timeline.Days==7)&&CompareHours.Children.Count==4,"seven-day selection controls comparison lines and all hourly grids together");
        CompareMetricBox.SelectedIndex=1;check(CompareHeatLegend.Text.Contains("失败比例"),"comparison hourly grids use a shared failure color scale");CompareMetricBox.SelectedIndex=0;
        await CaptureForVerificationAsync("-multi-compare-week");
        GlobalProtocolBox.SelectedIndex=1;await RefreshAsync();
        check(CompareChart.Series.Count==2&&CompareHours.Children.Count==4,"TCP comparison excludes disabled series without dropping their explanatory rows");
        await CaptureForVerificationAsync("-multi-compare-tcp");
        SelectProfile(a);ViewProtocolBox.SelectedIndex=0;await RefreshAsync();SelectProfile(c);SelectProfile(b);ViewProtocolBox.SelectedIndex=1;await RefreshAsync();
        check(_target?.Key==b.Secondary!.Key&&_timelineKey?.StartsWith(b.Secondary.Key)==true,"rapid target and protocol switching cannot render another target's timeline");
        TargetSearchBox.Text="IPv6";await Task.Delay(100);check(TargetList.Items.Count==1,"target list filters by name");TargetSearchBox.Text="";await Task.Delay(100);
        var revision=b.Copy();revision.Port=b.Port==65535?65534:b.Port+1;revision.RetainPrevious(b);_settings.Profiles[_settings.Profiles.FindIndex(p=>p.Id==b.Id)]=revision;
        SelectProfile(revision);_settings.Save();RebuildRevisions();RevisionBox.SelectedIndex=1;await RefreshAsync();
        check(_target?.Key==b.Secondary!.Key&&RevisionBox.Visibility==Visibility.Visible,"NativeAOT opens the previous TCP configuration after editing a port");
        var versions=Settings.Load().Profiles.Single(p=>p.Id==b.Id).PreviousMeasurements;
        check(versions.Count==b.PreviousMeasurements.Count+1&&versions[0].Port==b.Port,"editing a port retains both the previous service and all earlier revisions");
        _settings.Profiles[_settings.Profiles.FindIndex(p=>p.Id==b.Id)]=b;SelectProfile(a);RebuildTargetList();PeriodBox.SelectedIndex=0;GlobalProtocolBox.SelectedIndex=0;_multiLoaded=DateTimeOffset.MinValue;SetWorkspace(0);await RefreshAsync();
        var originalTheme=RootGrid.RequestedTheme;RootGrid.RequestedTheme=ElementTheme.Light;await CaptureForVerificationAsync("-multi-light");RootGrid.RequestedTheme=ElementTheme.Dark;await CaptureForVerificationAsync("-multi-dark");
        var size=AppWindow.Size;var scale=RootGrid.XamlRoot.RasterizationScale;AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1000*scale),(int)(750*scale)));
        await CaptureForVerificationAsync("-multi-compact");
        check(AllTargetsGrid.RowDefinitions.Count==5&&TargetList.ActualWidth>100,"compact native window retains navigation and all target rows");
        AppWindow.Resize(size);RootGrid.RequestedTheme=originalTheme;
        check(_manager.RunningProfiles==0,"multi-target verification leaves every monitor stopped");
        _settings.Save();stopServer.Cancel();await serving;
    }
}
