using Microsoft.UI.Xaml;
using TcpLatencyMonitor.App.Services;
using TcpLatencyMonitor.Core;

namespace TcpLatencyMonitor.App;

public sealed partial class MainWindow
{
    private void ApplyGlobalEditor()
    {
        var p=_settings.GlobalMonitoring;
        IntervalBox.Value=p.IntervalSeconds;TimeoutBox.Value=p.TimeoutMilliseconds;
        RoutesBox.IsChecked=p.EnableRoutes;RouteMinutesBox.Value=p.RouteMinutes;
        MaxHopsBox.Value=p.RouteMaxHops;RouteTimeoutBox.Value=p.RouteTimeoutMs;RouteBudgetBox.Value=p.RouteBudgetSeconds;
    }
    private void UnifiedSettings_Click(object sender,RoutedEventArgs e)
    {
        if(_stopping)return;
        if(_workspaceView!=3){ApplyGlobalEditor();GlobalSettingsStatus.Text="";}
        SetWorkspace(3);
    }
    private async void SaveGlobal_Click(object sender,RoutedEventArgs e)
    {
        try{await SaveGlobalSettingsAsync();}
        catch(Exception ex){GlobalSettingsStatus.Text="保存失败："+ex.Message;}
    }
    private async Task SaveGlobalSettingsAsync()
    {
        if(_stopping||_manager is null)return;
        var policy=new GlobalMonitorSettings(Integer(IntervalBox.Value,1,3600,"检测间隔"),Integer(TimeoutBox.Value,100,60000,"探测超时"),RoutesBox.IsChecked==true,
            Integer(RouteMinutesBox.Value,1,1440,"路由间隔"),Integer(MaxHopsBox.Value,1,64,"最大跳数"),Integer(RouteTimeoutBox.Value,100,5000,"每跳超时"),Integer(RouteBudgetBox.Value,1,180,"整轮路由上限"));
        policy.Validate();
        bool changed=policy!=_settings.GlobalMonitoring;
        var next=_settings.WithGlobalMonitoring(policy);
        // Complete validation and durable save before interrupting any running probe.
        next.Save();
        var running=_settings.Profiles.Where(p=>_manager.IsRunning(p.Id)).Select(p=>p.Id).ToHashSet();
        _stopping=true;TargetNavigation.IsEnabled=false;SaveGlobalButton.IsEnabled=false;
        var errors=new List<string>();
        try
        {
            _settings=next;
            if(changed)
            {
                await _manager.StopAllAsync();
                foreach(var p in _settings.Profiles.Where(p=>running.Contains(p.Id)))
                    try{await _manager.StartAsync(p);}catch(Exception ex){errors.Add(p.DisplayName+"："+ex.Message);}
            }
            if(_settings.Profiles.FirstOrDefault(p=>p.Id==_selectedProfileId) is { } selected)SelectProfile(selected);
            RebuildTargetList();_multiLoaded=DateTimeOffset.MinValue;
            GlobalSettingsStatus.Text=errors.Count==0?$"已应用到 {_settings.Profiles.Count} 个目标；新目标自动沿用。":"设置已保存；部分目标未能恢复："+string.Join("；",errors);
            StatusText.Text=GlobalSettingsStatus.Text;
        }
        finally{_stopping=false;TargetNavigation.IsEnabled=true;SaveGlobalButton.IsEnabled=true;SetWorkspace(3);UpdateLiveStates();}
    }
    private async Task VerifyGlobalSettingsAsync(Action<bool,string> check)
    {
        if(_manager is null)throw new InvalidOperationException("No manager");
        await _manager.StopAllAsync();var a=_settings.Profiles[0];var b=_settings.Profiles[1];
        var legacy=Settings.ParseExisting("""{"Version":3,"SelectedProfileId":"b","Profiles":[{"Id":"a","Address":"127.0.0.1","TimeoutMilliseconds":1000},{"Id":"b","Address":"127.0.0.2","IntervalSeconds":9,"TimeoutMilliseconds":2000,"RouteMinutes":17}]}""");
        check(legacy.Version==4&&legacy.GlobalMonitoring.IntervalSeconds==9&&legacy.Profiles.All(p=>p.TimeoutMilliseconds==2000&&p.RouteMinutes==17)&&legacy.Profiles[0].PreviousMeasurements[0].TimeoutMilliseconds==1000,"legacy migration uses selected policy once and preserves previous timeout history");
        ApplyGlobalEditor();IntervalBox.Value=2;SelectProfile(a);SelectProfile(b);
        check(IntervalBox.Value==2,"switching targets does not replace the unified settings editor");
        ApplyGlobalEditor();await _manager.StartAsync(a);SetWorkspace(3);UpdateLiveStates();
        check(IntervalBox.IsEnabled&&RoutesBox.IsEnabled&&UnifiedSettingsPanel.Visibility==Visibility.Visible&&TargetDetailPanel.Visibility==Visibility.Collapsed&&PeriodBox.Visibility==Visibility.Collapsed,"unified settings is editable during monitoring and has its own page");
        var before=a.TimeoutMilliseconds;TimeoutBox.Value=before+100;IntervalBox.Value=2;RoutesBox.IsChecked=false;
        await SaveGlobalSettingsAsync();
        var saved=Settings.Load();
        check(saved.GlobalMonitoring.IntervalSeconds==2&&saved.Profiles.All(p=>p.IntervalSeconds==2&&!p.EnableRoutes&&p.TimeoutMilliseconds==before+100),"saving global settings applies to all persisted targets");
        check(_manager.IsRunning(a.Id)&&!_manager.IsRunning(b.Id)&&_manager.RunningProfiles==1,"applying global settings restarts only previously running targets");
        check(saved.Profiles[0].PreviousMeasurements.Any(r=>r.TimeoutMilliseconds==before),"global timeout edits preserve historical measurement revisions");
        await _manager.StopAllAsync();AddTarget_Click(this,new RoutedEventArgs());AddressBox.Text="127.0.0.3";NameBox.Text="新目标 · 统一参数";ReadInputs();
        check(_settings.Profiles.Last().IntervalSeconds==2&&_settings.Profiles.Last().TimeoutMilliseconds==before+100&&!_settings.Profiles.Last().EnableRoutes,"new targets inherit the saved global policy");
        var empty=Settings.NewInstallation().WithGlobalMonitoring(new(12));check(empty.Profiles.Count==0&&empty.GlobalMonitoring.IntervalSeconds==12,"unified settings does not require any target");
        ApplyGlobalEditor();SetWorkspace(3);await CaptureForVerificationAsync("-unified-settings");
    }
}
