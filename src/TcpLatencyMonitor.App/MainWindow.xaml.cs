using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TcpLatencyMonitor.App.Services;
using TcpLatencyMonitor.Core;
using Windows.Graphics;
using Windows.UI;

namespace TcpLatencyMonitor.App;

public sealed partial class MainWindow : Window
{
    private readonly History _history = new(AppPaths.DatabasePath);
    private readonly DispatcherTimer _timer = new() { Interval=TimeSpan.FromSeconds(1) };
    private Settings _settings = new();
    private Target? _target,_primaryTarget,_secondaryTarget;
    private readonly Dictionary<string,Session> _sessions=new();
    private sealed class Session { public long Success,Failure,Error,Consecutive;public Sample? Last;public DateTimeOffset? FirstFailure; }
    private List<RouteRun> _routes=new();
    private bool _recordsDirty=true,_loadingRoutes,_refreshPending;
    private TaskCompletionSource? _refreshFinished;
    private NativeTray? _tray;
    private CancellationTokenSource? _cancellation;
    private bool _initialized, _hidden, _allowClose, _refreshing, _stopping;
    private long _sessionSuccess,_sessionFailure,_sessionError,_consecutive;
    private Sample? _lastSample;
    private DateTimeOffset? _failureStart;

    public MainWindow()
    {
        InitializeComponent();
        RootGrid.ActualThemeChanged+=(_,_)=>{if(_manager is not null){_allTargetsDirty=_comparisonDirty=true;if(!_hidden){RebuildTargetList();RenderVisibleMulti();UpdateLiveStates();}}};
        AppWindow.Title="IP 质量监控";
        AppWindow.Resize(new SizeInt32(1160,850));
        ConfigureTray();
        Activated+=FirstActivated;
        _timer.Tick+=async (_,_) => { UpdateLiveStates();if(!_hidden)await RefreshAsync(); };
        Closed+=(_,_)=> { _timer.Stop();_cancellation?.Cancel();if(_manager is not null)_=_manager.StopAllAsync();CancelMetadata();_metadataClient?.Dispose();_tray?.Dispose(); };
    }
    private async void FirstActivated(object sender,WindowActivatedEventArgs args)
    {
        if(_initialized)return; _initialized=true;Activated-=FirstActivated;
        try
        {
            if(!RootGrid.IsLoaded)
            {
                var loaded=new TaskCompletionSource();
                RoutedEventHandler? ready=null;
                ready=(_,_)=>{RootGrid.Loaded-=ready;loaded.TrySetResult();};
                RootGrid.Loaded+=ready;
                await loaded.Task;
            }
            var scale=RootGrid.XamlRoot?.RasterizationScale??1;
            var workArea=Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            var width=Math.Min((int)(1220*scale),workArea.Width-40);
            var height=Math.Min((int)(1000*scale),workArea.Height-40);
            AppWindow.MoveAndResize(new RectInt32(workArea.X+(workArea.Width-width)/2,workArea.Y+(workArea.Height-height)/2,width,height));
            StartupDiagnostics.Write($"Initial layout: scale={scale}, workArea={workArea.Width}x{workArea.Height}, window={width}x{height}");
            await Task.Run(_history.Initialize);
            _settings=Settings.Load();
            if(_settings.LoadError is not null)throw new IOException("配置未能读取，原文件已保留："+_settings.LoadError);
            _metadataClient=new NodeMetadataClient(_history);MetadataBox.IsChecked=_settings.EnableNodeMetadata;
            InitializeGeoSettings();
            BackgroundMetadataBox.IsChecked=_settings.BackgroundNodeMetadata;BackgroundMetadataBox.IsEnabled=_settings.EnableNodeMetadata;
            InitializeAnnotationService();PeriodBox.SelectedIndex=_settings.TimelineDays==7?1:0;
            ApplyGlobalEditor();
            InitializeMultiTargetMonitor();
            var initial=_settings.Profiles.FirstOrDefault(p=>p.Id==_settings.SelectedProfileId);
            if(initial is not null)SelectProfile(initial);
            else{ApplyEditor(new TargetProfile());TargetEditor.IsExpanded=true;}
            RebuildTargetList();SetRunning(false);_timer.Start();
            SetWorkspace(_settings.Profiles.Count==1?1:0);
            StatusText.Text="就绪 · 目标独立监测 · 历史仅保存在本机";
            foreach(var profile in _settings.Profiles.Where(p=>p.ResumeOnLaunch).ToArray())
            {try{await _manager!.StartAsync(profile);}catch(Exception ex){ShowError(ex);}}
            UpdateLiveStates();await RefreshAsync();
            StartupDiagnostics.Write("Monitor initialized.");
            if(Environment.GetEnvironmentVariable("IPQUALITY_MEMORY_AUDIT")=="1"){await RunRenderingMemoryCheckAsync();return;}
            if(Environment.GetEnvironmentVariable("TCP_MONITOR_V4_SELF_TEST")=="1")await VerifyNativeV4Async();
            if(Environment.GetEnvironmentVariable("TCP_MONITOR_SMOKE_TEST")=="1")await VerifyLifecycleAsync();
            await CaptureForVerificationAsync();
            if(Environment.GetEnvironmentVariable("TCP_MONITOR_SMOKE_TEST")=="1")
            {
                DashboardScroll.ChangeView(null,Math.Min(400,DashboardScroll.ScrollableHeight),null,true);
                await CaptureForVerificationAsync("-details");
                DashboardScroll.ChangeView(null,0,null,true);
                PageBox.SelectedIndex=1;await RefreshAsync();await CaptureForVerificationAsync("-routes");
                PageBox.SelectedIndex=2;await RefreshAsync();await CaptureForVerificationAsync("-events");
                PageBox.SelectedIndex=0;
            }
        }
        catch(Exception ex) { ShowError(ex);TargetEditor.IsExpanded=true;StatusText.Text="初始化失败："+ex.Message; }
    }
    private static int Integer(double value,int min,int max,string label)
    {
        if(!double.IsFinite(value)||value!=Math.Truncate(value)||value<min||value>max)
            throw new ArgumentException($"{label}必须是 {min}–{max} 之间的整数。");
        return (int)value;
    }
    private async Task CaptureForVerificationAsync(string suffix="",FrameworkElement? element=null)
    {
        // Opt-in test hook: renders only this application's UI, never the desktop.
        var path=Environment.GetEnvironmentVariable("TCP_MONITOR_CAPTURE_PATH");
        if(string.IsNullOrEmpty(path))return;
        if(suffix.Length>0)path=Path.Combine(Path.GetDirectoryName(path)!,Path.GetFileNameWithoutExtension(path)+suffix+".png");
        try
        {
            await Task.Delay(700);
            var bitmap=new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await bitmap.RenderAsync(element??RootGrid);
            var buffer=await bitmap.GetPixelsAsync();var pixels=new byte[buffer.Length];
            using(var reader=Windows.Storage.Streams.DataReader.FromBuffer(buffer))reader.ReadBytes(pixels);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);File.WriteAllBytes(path,Array.Empty<byte>());
            var file=await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            using var stream=await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
            var encoder=await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels);
            await encoder.FlushAsync();StartupDiagnostics.Write($"UI verification render: {bitmap.PixelWidth}x{bitmap.PixelHeight}; viewport {RootGrid.ActualWidth}x{RootGrid.ActualHeight}");
        }
        catch(Exception ex){StartupDiagnostics.Write("UI verification render failed.",ex);}
    }
    private async Task VerifyLifecycleAsync()
    {
        // Integration test entry point. Refuse any non-loopback test destination.
        if(_target is null || !System.Net.IPAddress.IsLoopback(System.Net.IPAddress.Parse(_target.Address)))
            throw new InvalidOperationException("UI self-test requires a loopback target.");
        var report=new List<string>();
        void Check(bool condition,string label){if(!condition)throw new InvalidOperationException("UI self-test failed: "+label);report.Add("PASS "+label);}
        VerifyAnnotationSettings(Check);
        using var server=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);server.Start();
        using var serverStop=new CancellationTokenSource();
        var serving=Task.Run(async()=>{try{while(true){using var socket=await server.AcceptSocketAsync(serverStop.Token);}}catch(OperationCanceledException){}});
        if(_settings.Mode=="Both"||_settings.Mode=="Tcp")PortBox.Value=((System.Net.IPEndPoint)server.LocalEndpoint).Port;
        await StartAsync();await Task.Delay(2300);
        Check(_sessionSuccess>=2 && !AddressBox.IsEnabled && StopButton.IsEnabled,"start and live samples");
        if(_settings.Mode=="Both")
        {
            Check(_sessions.TryGetValue(_secondaryTarget!.Key,out var tcp)&&tcp.Success>=2,"both protocols sampled independently");
            ViewProtocolBox.SelectedIndex=1;await RefreshAsync();Check(_target?.Protocol==ProbeProtocol.Tcp,"switch to TCP statistics");
            ViewProtocolBox.SelectedIndex=0;await RefreshAsync();Check(_target?.Protocol==ProbeProtocol.Icmp,"switch back to ICMP statistics");
        }
        var before=_sessionSuccess;
        HideToTray();await Task.Delay(2200);
        Check(_hidden && _sessionSuccess>before,"hidden window continues sampling");
        RestoreFromTray();await StopAsync();
        var stopped=_sessionSuccess;await Task.Delay(1300);
        Check(_sessionSuccess==stopped && _cancellation is null && AddressBox.IsEnabled,"stop cancels further samples and unlocks settings");
        await StartAsync();await Task.Delay(1300);await StopAsync();
        Check(_sessionSuccess>=1,"restart after stop");
        var path=Path.Combine(AppPaths.DataDirectory,"smoke-export.csv");
        var exportTarget=_target??throw new InvalidOperationException("Missing test target");
        var count=await Task.Run(()=>_history.Export(exportTarget,path,DateTimeOffset.UtcNow));
        Check(count>0&&File.ReadAllText(path).Contains(exportTarget.Protocol==ProbeProtocol.Icmp?"ICMP":"TCP connect"),"export saved history");
        if(_settings.EnableRoutes)Check(_history.LoadRoutes(_primaryTarget!,1).Count>0,"native route history persisted");
        Check(_tray is not null,"native tray initialized");
        var bundle=Path.Combine(AppPaths.DataDirectory,"smoke-diagnostics.zip");
        if(File.Exists(bundle))File.Delete(bundle);
        await Task.Run(()=>_history.ExportBundle(_target!,bundle,DateTimeOffset.UtcNow,RouteTarget));
        using(var archive=System.IO.Compression.ZipFile.OpenRead(bundle))Check(archive.GetEntry("routes.json") is not null,"diagnostic bundle export in NativeAOT app");
        await VerifyAnnotationsAsync(Check);
        await VerifyOverviewFeaturesAsync(Check);
        await VerifyRouteRepliesAsync(Check);
        await VerifyGeoUiAsync(Check);
        await VerifyMultiTargetUiAsync(Check);
        await VerifyGlobalSettingsAsync(Check);
        await VerifyRenderingAsync(Check);
        serverStop.Cancel();await serving;
        File.WriteAllLines(Path.Combine(AppPaths.DataDirectory,"smoke-results.txt"),report);
        StatusText.Text="本机自检完成 · 测试数据不进入发布包";
        StartupDiagnostics.Write(string.Join("; ",report));
    }
    private void ReadInputs()
    {
        if(_settings.LoadError is not null)throw new IOException("原配置未能读取，已保留原文件："+_settings.LoadError);
        var policy=_settings.GlobalMonitoring;var timeout=policy.TimeoutMilliseconds;
        if(!double.IsFinite(FactorBox.Value)||FactorBox.Value<1.1||FactorBox.Value>10)throw new ArgumentException("延迟相对倍数必须在 1.1–10 之间。");
        var mode=ProtocolBox.SelectedIndex==1?"Tcp":ProtocolBox.SelectedIndex==2?"Both":"Icmp";
        var target=Target.Parse(AddressBox.Text,PortBox.Value,mode=="Tcp"?ProbeProtocol.Tcp:ProbeProtocol.Icmp,timeout);
        _secondaryTarget=mode=="Both"?Target.Parse(AddressBox.Text,PortBox.Value,ProbeProtocol.Tcp,timeout):null;
        var settings=new Settings { Id=_selectedProfileId??Guid.NewGuid().ToString("N"),Name=NameBox.Text.Trim(),Address=target.Address,Port=mode=="Icmp"?443:Integer(PortBox.Value,1,65535,"端口"),Mode=mode,
            GlobalMonitoring=policy,IntervalSeconds=policy.IntervalSeconds,
            TimeoutMilliseconds=timeout,EnableRoutes=policy.EnableRoutes,EnableNodeMetadata=MetadataBox.IsChecked==true,BackgroundNodeMetadata=BackgroundMetadataBox.IsChecked==true,TimelineDays=_timelineDays,RouteMinutes=policy.RouteMinutes,
            RouteMaxHops=policy.RouteMaxHops,RouteTimeoutMs=policy.RouteTimeoutMs,RouteBudgetSeconds=policy.RouteBudgetSeconds,
            Failures=Integer(FailuresBox.Value,2,20,"失败确认次数"),Recoveries=Integer(RecoveriesBox.Value,2,20,"恢复确认次数"),
            LatencyIncreaseMs=Integer(IncreaseBox.Value,1,10000,"延迟增幅"),LatencyFactor=FactorBox.Value,LatencySustainSeconds=Integer(SustainBox.Value,10,600,"延迟持续时间"),
            ResumeOnLaunch=ResumeBox.IsChecked==true };
        settings.Profiles=_settings.Profiles;settings.ArchivedProfiles=_settings.ArchivedProfiles;
        settings.GeoPrimary=_settings.GeoPrimary;settings.GeoCrossCheck=_settings.GeoCrossCheck;settings.GeoRefreshHours=_settings.GeoRefreshHours;
        settings.SelectedProfileId=settings.Id;
        var previous=settings.Profiles.FirstOrDefault(p=>p.Id==settings.Id);
        if(previous is not null)settings.RetainPrevious(previous);
        settings.Validate();
        var list=new List<TargetProfile>(settings.Profiles);int profileIndex=list.FindIndex(p=>p.Id==settings.Id);
        if(profileIndex>=0)list[profileIndex]=settings.Copy();else list.Add(settings.Copy());
        settings.Profiles=list;settings.Save();_selectedProfileId=settings.Id;_viewRevision=null;
        bool changed=_target?.Key!=target.Key;
        _primaryTarget=target;_target=mode=="Both"&&ViewProtocolBox.SelectedIndex==1?_secondaryTarget:target;_settings=settings;AddressBox.Text=target.Address;
        TargetText.Text=_target!.Label;AvailabilityLabel.Text=_target.Protocol==ProbeProtocol.Icmp?"近 5 分钟回应率":"近 5 分钟建连成功率";ExportButton.IsEnabled=true;
        InputError.Visibility=Visibility.Collapsed;
        if(changed) { _lastSample=null;LatestValue.Text="—";LatestHint.Text="正在读取历史";_recordsDirty=true;_routes.Clear(); }
        _selectionVersion++;_multiLoaded=DateTimeOffset.MinValue;RebuildTargetList();RebuildRevisions();SetWorkspace(1);
    }
    private async void Start_Click(object sender,RoutedEventArgs e) => await StartAsync();
    private async Task StartAsync()
    {
        if(_manager is null||_stopping||(_selectedProfileId is not null&&_manager.IsRunning(_selectedProfileId)))return;
        try
        {
            ReadInputs();await _manager.StartAsync(_settings.Profiles.Single(p=>p.Id==_selectedProfileId));
            TargetEditor.IsExpanded=false;UpdateLiveStates();_multiLoaded=DateTimeOffset.MinValue;await RefreshAsync();
        }
        catch(Exception ex){ShowError(ex);TargetEditor.IsExpanded=true;}
    }
    private void DisplaySession(Session state)
    {
        var sample=state.Last;if(sample is null)return;
        _sessionSuccess=state.Success;_sessionFailure=state.Failure;_sessionError=state.Error;_consecutive=state.Consecutive;_failureStart=state.FirstFailure;
        _lastSample=sample;
        SessionText.Text=$"本次运行 · 成功 {_sessionSuccess} / 失败 {_sessionFailure} / 本地错误 {_sessionError}";
        var span=_failureStart.HasValue?sample.Time-_failureStart.Value:TimeSpan.Zero;
        FailureText.Text=$"连续失败 · {_consecutive} 次"+(_consecutive>1?$"，采样跨度 {span.TotalSeconds:F0} 秒":"");
        if(_workspaceView==1&&PageBox.SelectedIndex==0)SetLatest(sample);
    }
    private void SetLatest(Sample sample)
    {
        LatestValue.Text=sample.LatencyMs is double ms?(ms<1?"<1 ms":$"{ms:F1} ms"):Sample.Label(sample.Status);
        LatestValue.Foreground=StatusBrush(sample.Status);
        LatestHint.Text=$"{sample.Time.ToLocalTime():MM-dd HH:mm:ss} · {Sample.Label(sample.Status)}";
    }
    private async void Stop_Click(object sender,RoutedEventArgs e)=>await StopAsync();
    private async Task StopAsync()
    {
        if(_manager is null||_stopping||_selectedProfileId is null)return;
        _stopping=true;StopButton.IsEnabled=false;var id=_selectedProfileId;
        try{await _manager.StopAsync(id);}
        finally{_stopping=false;UpdateLiveStates();_recordsDirty=true;_multiLoaded=DateTimeOffset.MinValue;}
        StatusText.Text="所选目标已暂停 · 历史已保存 · 其他目标继续监测";
        await RefreshAsync();
    }
    private static void SetText(TextBlock control,string value){if(control.Text!=value)control.Text=value;}
    private static void SetEnabled(Control control,bool value){if(control.IsEnabled!=value)control.IsEnabled=value;}
    private static void SetContent(ContentControl control,string value){if(control.Content as string!=value)control.Content=value;}
    private void SetRunning(bool running)
    {
        SetEnabled(StartButton,!running&&!_storageFailed);SetEnabled(StopButton,running);SetEnabled(NameBox,!running);SetEnabled(RemoveTargetButton,_selectedProfileId is not null);
        foreach(var control in new Control[]{AddressBox,PortBox,ProtocolBox,ResumeBox,HistoryButton,LegacyButton,AdvancedBox})SetEnabled(control,!running);
        SetEnabled(RouteButton,running&&_settings.EnableRoutes);
    }
    private async void History_Click(object sender,RoutedEventArgs e)
    {
        try { ReadInputs();await RefreshAsync();StatusText.Text="设置已保存 · 按协议和探测参数查看历史"; }
        catch(Exception ex) { ShowError(ex); }
    }
    private Task RefreshAsync()
    {
        if(!_initialized||_manager is null)return Task.CompletedTask;
        if(_refreshing){_refreshPending=true;return _refreshFinished!.Task;}
        _refreshing=true;_refreshFinished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task=_refreshFinished.Task;_=RefreshLoopAsync();return task;
    }
    private async Task RefreshLoopAsync()
    {
        var finished=_refreshFinished!;
        try{do{_refreshPending=false;await RefreshOnceAsync();}while(_refreshPending&&!_hidden);}
        finally{_refreshing=false;finished.TrySetResult();}
    }
    private async Task RefreshOnceAsync()
    {
        if(_hidden||_workspaceView==3)return;
        if(_workspaceView!=1){await RefreshMultiAsync();return;}
        var target=_target;if(target is null)return;
        int selection=_selectionVersion;var now=DateTimeOffset.UtcNow;
        if(_detailLoadedKey==target.Key&&now-_detailLoaded<TimeSpan.FromSeconds(15)&&!_recordsDirty&&_timelineKey is not null)return;
        _detailLoadedKey=target.Key;_detailLoaded=now;
        try
        {
            if(PageBox.SelectedIndex>0){await RefreshDetailsAsync();return;}
            var dashboard=await Task.Run(()=>_history.Load(target,DateTimeOffset.UtcNow));
            if(_hidden||_workspaceView!=1||_target?.Key!=target.Key||selection!=_selectionVersion){_detailLoadedKey=null;return;}
            AvailabilityValue.Text=Percent(dashboard.FiveMinutes.Availability);
            AvailabilityHint.Text=$"成功 {dashboard.FiveMinutes.Successes} / {dashboard.FiveMinutes.Attempts} 次有效尝试";
            P95Value.Text=dashboard.P95Hour is double p95?$"{p95:F1}":"—";
            if(_lastSample is null && dashboard.Recent.Count>0)SetLatest(dashboard.Recent[0]);
            await RefreshTimelineAsync(target);if(_hidden||_workspaceView!=1||selection!=_selectionVersion){_detailLoadedKey=null;return;}await RefreshOverviewRouteAsync();if(_target?.Key!=target.Key||_hidden||_workspaceView!=1||selection!=_selectionVersion){_detailLoadedKey=null;return;}RenderSummary(dashboard);RenderRecent(dashboard.Recent);
            var ordered=dashboard.Recent.OrderBy(s=>s.Time).ToArray();var diffs=new List<double>();
            for(int i=1;i<ordered.Length;i++)if(ordered[i].Status==ProbeStatus.Success&&ordered[i-1].Status==ProbeStatus.Success&&ordered[i].Context==ordered[i-1].Context&&ordered[i].Time-ordered[i-1].Time<=TimeSpan.FromSeconds(_settings.IntervalSeconds+2))diffs.Add(Math.Abs(ordered[i].LatencyMs!.Value-ordered[i-1].LatencyMs!.Value));
            VariationText.Text=$"近 1 小时 P50 {Milliseconds(dashboard.P50Hour)} · 最低 {Milliseconds(dashboard.Hour.Minimum)} · 最高 {Milliseconds(dashboard.Hour.Maximum)}\n最近采样延迟波动：{(diffs.Count==0?"—":$"{diffs.Average():F1} ms")} · 近 1 分钟记录 {dashboard.Minute.Attempts+dashboard.Minute.LocalErrors} 次（本地错误 {dashboard.Minute.LocalErrors}）";
            _recordsDirty=false;
        }
        catch(Exception ex) { StartupDiagnostics.Write("History refresh failed.",ex);StatusText.Text="历史读取失败："+ex.Message; }
    }
    private static string Percent(double? value)=>value is double v?$"{v:F1}%":"—";
    private static string Milliseconds(double? value)=>value is double v?$"{v:F1} ms":"—";
    private static SolidColorBrush Brush(byte r,byte g,byte b)=>new(Color.FromArgb(255,r,g,b));
    private static SolidColorBrush StatusBrush(ProbeStatus status)=>status switch
    { ProbeStatus.Success=>Brush(24,157,153),ProbeStatus.LocalError=>Brush(133,142,151),_=>Brush(213,78,78) };
    private TextBlock[,]? _summaryCells;
    private readonly List<(Grid Grid,TextBlock[] Cells)> _recentRows=new();
    private TextBlock? _recentEmpty;
    private void RenderSummary(Dashboard d)
    {
        if(_summaryCells is null)
        {
        _summaryCells=new TextBlock[5,5];
        foreach(var width in new[]{1d,1.2,1,1.2,1})SummaryGrid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(width,GridUnitType.Star)});
        for(int r=0;r<5;r++)
        {
            SummaryGrid.RowDefinitions.Add(new RowDefinition());
            for(int c=0;c<5;c++){var text=new TextBlock{FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis};Grid.SetRow(text,r);Grid.SetColumn(text,c);SummaryGrid.Children.Add(text);_summaryCells[r,c]=text;}
        }
        }
        string[] headers={"时间范围","平均耗时","成功率","失败 / 尝试","本地错误"};
        for(int c=0;c<5;c++){_summaryCells[0,c].Text=headers[c];_summaryCells[0,c].Foreground=SecondaryBrush;}
        var rows=new[]{("近 1 分钟",d.Minute),("近 24 小时",d.Day),("近 7 天",d.Week),("近 30 天",d.Month)};
        for(int r=0;r<rows.Length;r++)
        {
            var (name,total)=rows[r];var values=new[]{name,Milliseconds(total.Average),Percent(total.Availability),$"{total.Attempts-total.Successes} / {total.Attempts}",total.LocalErrors.ToString()};
            for(int c=0;c<5;c++)_summaryCells[r+1,c].Text=values[c];
        }
    }
    private void RenderRecent(IReadOnlyList<Sample> samples)
    {
        if(_recentEmpty is null){_recentEmpty=new TextBlock{Text="当前目标暂无采样记录。",FontSize=12};RecentPanel.Children.Add(_recentEmpty);}
        _recentEmpty.Visibility=samples.Count==0?Visibility.Visible:Visibility.Collapsed;
        int count=Math.Min(20,samples.Count);
        while(_recentRows.Count<count)
        {
            var grid=new Grid{ColumnSpacing=10};foreach(var width in new[]{135d,100,90})grid.ColumnDefinitions.Add(new(){Width=new GridLength(width)});grid.ColumnDefinitions.Add(new());
            var cells=new TextBlock[4];for(int c=0;c<4;c++){cells[c]=new(){FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis};Grid.SetColumn(cells[c],c);grid.Children.Add(cells[c]);}
            RecentPanel.Children.Add(grid);_recentRows.Add((grid,cells));
        }
        for(int r=0;r<_recentRows.Count;r++)
        {
            var row=_recentRows[r];row.Grid.Visibility=r<count?Visibility.Visible:Visibility.Collapsed;if(r>=count)continue;
            var sample=samples[r];var values=new[]{sample.Time.ToLocalTime().ToString("MM-dd HH:mm:ss"),Sample.Label(sample.Status),Milliseconds(sample.LatencyMs),sample.Detail};
            for(int c=0;c<4;c++){row.Cells[c].Text=values[c];SetTip(row.Cells[c],values[c]);}
            row.Cells[1].Foreground=StatusBrush(sample.Status);
        }
    }
    private void ShowError(Exception ex)
    {
        InputError.Text=ex.Message;InputError.Visibility=Visibility.Visible;
        if(ex is not ArgumentException)StartupDiagnostics.Write("Operation failed.",ex);
    }
    private async void Export_Click(object sender,RoutedEventArgs e)
    {
        var target=_target;if(target is null)return;ExportButton.IsEnabled=false;
        try
        {
            var directory=Path.Combine(AppPaths.DataDirectory,"exports");Directory.CreateDirectory(directory);
            var path=Path.Combine(directory,$"ip-quality-{target.Protocol}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip");
            var routeTarget=RouteTarget;
            var count=await Task.Run(()=>_history.ExportBundle(target,path,DateTimeOffset.UtcNow,routeTarget));
            StatusText.Text=$"已导出 {count} 条记录：{path}";
            Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{path}\""){UseShellExecute=true});
        }
        catch(Exception ex){ShowError(ex);}
        finally{ExportButton.IsEnabled=true;}
    }
    private void OpenData_Click(object sender,RoutedEventArgs e)
    { try {Directory.CreateDirectory(AppPaths.DataDirectory);Process.Start(new ProcessStartInfo(AppPaths.DataDirectory){UseShellExecute=true});}catch(Exception ex){ShowError(ex);} }
    private void ConfigureTray()
    {
        try
        {
            var icon=TrayIconAsset.EnsureCreated();AppWindow.SetIcon(icon);
            _tray=new NativeTray(WinRT.Interop.WindowNative.GetWindowHandle(this),icon,
                ()=>DispatcherQueue.TryEnqueue(RestoreFromTray),
                ()=>DispatcherQueue.TryEnqueue(async()=>{if(!_initialized||_stopping)return;if(_manager?.RunningProfiles>0)await PauseAllAsync();else await StartAllAsync();}),
                ()=>DispatcherQueue.TryEnqueue(async()=>await ExitAsync()),()=>_manager?.RunningProfiles>0);
            _tray.PowerChanged+=suspended=>_manager?.PowerChanged(suspended);
        }
        catch(Exception ex){StartupDiagnostics.Write("Tray unavailable; window close will exit.",ex);_tray?.Dispose();_tray=null;}
        AppWindow.Closing+=(sender,args)=>
        {
            if(_allowClose)return;
            args.Cancel=true;
            if(_tray is null){_=ExitAsync();return;}
            HideToTray();
        };
    }
    private void HideToTray()
    { if(_annotationService?.Mode==1)_annotationService.CancelRequests();CancelMetadata();_hidden=true;RootGrid.Visibility=Visibility.Collapsed;AppWindow.Hide(); }
    internal void RestoreFromTray()
    { _hidden=false;RootGrid.Visibility=Visibility.Visible;AppWindow.Show();Activate();_allTargetsDirty=_comparisonDirty=true;_detailLoadedKey=null;UpdateLiveStates();RenderVisibleMulti();_=RefreshAsync(); }
    private async void Exit_Click(object sender,RoutedEventArgs e)=>await ExitAsync();
    private async Task ExitAsync()
    { if(_allowClose||_stopping)return;_stopping=true;_timer.Stop();CancelMetadata();if(_manager is not null){await _manager.DisposeAsync();_manager=null;}if(_annotationService is not null){await _annotationService.DisposeAsync();_annotationService=null;}await _history.DisposeAsync();_allowClose=true;_tray?.Dispose();_tray=null;Close(); }
}
