using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TcpLatencyMonitor.App.Controls;
using TcpLatencyMonitor.App.Services;
using TcpLatencyMonitor.Core;

namespace TcpLatencyMonitor.App;

public sealed partial class MainWindow
{
    private MultiTargetMonitor? _manager;
    private string? _selectedProfileId;
    private MeasurementRevision? _viewRevision;
    private string ViewMode=>_viewRevision?.Mode??_settings.Mode;
    private int _workspaceView=1,_selectionVersion,_liveDirty=1;
    private bool _selectingProfile,_rebuildingList,_storageFailed;
    private DateTimeOffset _multiLoaded,_detailLoaded;
    private string? _detailLoadedKey,_multiKey;
    private MultiOverview? _multiOverview;
    private CancellationTokenSource? _overviewRead;
    private readonly HashSet<string> _compared=new();
    private readonly Dictionary<string,TextBlock> _listStates=new(),_rowStates=new(),_rowLatest=new();
    private readonly Dictionary<string,Button> _rowActions=new();
    private readonly SolidColorBrush _secondaryDark=Brush(170,178,189),_secondaryLight=Brush(91,100,113),_warningBrush=Brush(207,100,60);
    private SolidColorBrush SecondaryBrush=>RootGrid.ActualTheme==ElementTheme.Dark?_secondaryDark:_secondaryLight;
    private bool _allTargetsDirty=true,_comparisonDirty=true;
    private readonly Dictionary<string,OverviewRow> _overviewRows=new();
    private readonly List<TextBlock> _overviewHeaders=new();
    private sealed class OverviewRow
    {
        public required CheckBox Select; public required Button Open,Action;
        public required TextBlock Name,Address,State,Latest,Loss,Route;
        public required MultiLatencyChart Trend;
        public IEnumerable<FrameworkElement> Cells=>new FrameworkElement[]{Select,Open,State,Latest,Loss,Trend,Route,Action};
    }
    private sealed class CachedToolTip
    {
        public string Text="";
        public readonly TextBlock Content=new(){TextWrapping=TextWrapping.Wrap,MaxWidth=720};
        public readonly ToolTip Tip=new();
        public CachedToolTip(){Tip.Content=Content;Tip.Opened+=(_,_)=>Refresh();}
        public void Refresh(){if(Content.Text!=Text)Content.Text=Text;}
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject,CachedToolTip> _toolTips=new();
    private void SetTip(DependencyObject element,string text)
    {
        var cached=_toolTips.GetValue(element,key=>
        {
            var value=new CachedToolTip();ToolTipService.SetToolTip(key,value.Tip);
            if(key is UIElement ui)ui.PointerEntered+=(_,_)=>value.Refresh();
            return value;
        });
        cached.Text=text;if(cached.Tip.IsOpen)cached.Refresh();
    }
    private void RenderVisibleMulti()
    {
        if(_hidden||_multiOverview is null||_multiOverview.Days!=_timelineDays||_multiOverview.Protocol!=(GlobalProtocolBox.SelectedIndex==1?ProbeProtocol.Tcp:ProbeProtocol.Icmp))return;
        if(_workspaceView==0&&_allTargetsDirty)RenderAllTargets();
        if(_workspaceView==2&&_comparisonDirty)RenderComparison();
    }

    private void InitializeMultiTargetMonitor()
    {
        _manager=new(_history);
        _manager.Changed+=()=>Interlocked.Exchange(ref _liveDirty,1);
        _manager.RouteSaved+=route=>
        {
            if(_annotationService is not null)_=_annotationService.Request(route,false);
            DispatcherQueue.TryEnqueue(()=>{_overviewRouteDirty=true;_recordsDirty=true;_multiLoaded=DateTimeOffset.MinValue;});
        };
        _manager.TargetFailed+=(id,ex)=>DispatcherQueue.TryEnqueue(async()=>
        {
            if(_manager is not null)await _manager.StopAsync(id);UpdateLiveStates();
            StatusText.Text=$"目标监测中断：{_settings.Profiles.FirstOrDefault(p=>p.Id==id)?.DisplayName} · {ex.Message}";
            StartupDiagnostics.Write("Target monitor failed.",ex);
        });
        _manager.StorageFailed+=ex=>DispatcherQueue.TryEnqueue(async()=>
        {
            _storageFailed=true;if(_manager is not null)await _manager.StopAllAsync();UpdateLiveStates();
            StartAllButton.IsEnabled=false;ShowError(ex);TargetEditor.IsExpanded=true;
            StatusText.Text="记录写入失败，所有目标已停止。请检查磁盘空间，解决后重新打开软件。";
        });
    }
    private void ApplyEditor(TargetProfile profile)
    {
        _selectingProfile=true;
        try
        {
            NameBox.Text=profile.Name;AddressBox.Text=profile.Address;PortBox.Value=profile.Port;
            ProtocolBox.SelectedIndex=profile.Mode=="Tcp"?1:profile.Mode=="Both"?2:0;
            FailuresBox.Value=profile.Failures;RecoveriesBox.Value=profile.Recoveries;IncreaseBox.Value=profile.LatencyIncreaseMs;FactorBox.Value=profile.LatencyFactor;SustainBox.Value=profile.LatencySustainSeconds;
            ResumeBox.IsChecked=profile.ResumeOnLaunch;
            UpdateProtocolControls();
        }
        finally{_selectingProfile=false;}
    }
    private void SelectProfile(TargetProfile profile)
    {
        CancelMetadata();_overviewRead?.Cancel();_selectionVersion++;
        _selectedProfileId=profile.Id;_settings.SelectedProfileId=profile.Id;profile.CopyTo(_settings);_viewRevision=null;
        ApplyEditor(profile);ApplyDisplayTargets(profile);RebuildRevisions();
        TargetEditor.IsExpanded=false;InputError.Visibility=Visibility.Collapsed;
        _recordsDirty=_overviewRouteDirty=true;_routes.Clear();_lastSample=null;_timelineKey=_detailLoadedKey=null;
        _displayedRoute=null;_latestRouteId=null;RouteRows.Children.Clear();SetOverviewTable(null,null);
        LatestValue.Text="—";LatestHint.Text="正在读取历史";Chart.SetData(Array.Empty<Bucket>());_timeline=null;_timelineKey=null;_hourMatrix?.Clear();
        SessionText.Text="本次运行 · 暂无采样";FailureText.Text="连续失败 · —";
        _cancellation?.Dispose();_cancellation=null;UpdateLiveStates();SetWorkspace(1);
    }
    private void ApplyDisplayTargets(TargetProfile profile)
    {
        _primaryTarget=profile.Primary;_secondaryTarget=profile.Secondary;
        _target=profile.Mode=="Both"&&ViewProtocolBox.SelectedIndex==1?_secondaryTarget:_primaryTarget;
        ViewProtocolBox.Visibility=profile.Mode=="Both"?Visibility.Visible:Visibility.Collapsed;
        TargetText.Text=_target!.Label+(_viewRevision is null?"":" · 历史配置");
        AvailabilityLabel.Text=_target.Protocol==ProbeProtocol.Icmp?"近 5 分钟回应率":"近 5 分钟建连成功率";
        ExportButton.IsEnabled=true;
    }
    private void RebuildRevisions()
    {
        _selectingProfile=true;
        try
        {
            RevisionBox.Items.Clear();RevisionBox.Items.Add(new ComboBoxItem{Content="当前检测配置"});
            var profile=_settings.Profiles.FirstOrDefault(p=>p.Id==_selectedProfileId);
            if(profile is not null)foreach(var revision in profile.PreviousMeasurements)
                RevisionBox.Items.Add(new ComboBoxItem{Content=$"{revision.SavedAt.ToLocalTime():MM-dd HH:mm} 前 · {revision.Address} · {revision.Mode} · TCP {revision.Port} · 超时 {revision.TimeoutMilliseconds} ms",Tag=revision});
            RevisionBox.SelectedIndex=0;RevisionBox.Visibility=RevisionBox.Items.Count>1?Visibility.Visible:Visibility.Collapsed;
        }
        finally{_selectingProfile=false;}
    }
    private async void Revision_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_selectingProfile||_selectedProfileId is null)return;
        var profile=_settings.Profiles.FirstOrDefault(p=>p.Id==_selectedProfileId)?.Copy();if(profile is null)return;
        _viewRevision=(RevisionBox.SelectedItem as ComboBoxItem)?.Tag as MeasurementRevision;
        if(_viewRevision is not null){profile.Address=_viewRevision.Address;profile.Port=_viewRevision.Port;profile.Mode=_viewRevision.Mode;profile.TimeoutMilliseconds=_viewRevision.TimeoutMilliseconds;}
        _selectionVersion++;CancelMetadata();ApplyDisplayTargets(profile);_timelineKey=_detailLoadedKey=null;_lastSample=null;_recordsDirty=_overviewRouteDirty=true;
        LatestValue.Text="—";await RefreshAsync();
    }
    private void RebuildTargetList()
    {
        if(TargetList is null)return;_rebuildingList=true;
        try
        {
            TargetList.Items.Clear();_listStates.Clear();string search=TargetSearchBox.Text.Trim();
            foreach(var profile in _settings.Profiles.Where(p=>search.Length==0||p.DisplayName.Contains(search,StringComparison.OrdinalIgnoreCase)||p.Address.Contains(search,StringComparison.OrdinalIgnoreCase)))
            {
                var content=new StackPanel{Spacing=2};content.Children.Add(new TextBlock{Text=profile.DisplayName,FontSize=13,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis});
                var status=new TextBlock{Text=profile.Address,FontSize=10,Foreground=SecondaryBrush,TextTrimming=TextTrimming.CharacterEllipsis};content.Children.Add(status);_listStates[profile.Id]=status;
                var item=new ListViewItem{Content=content,Tag=profile.Id,Padding=new Thickness(8,6,8,6),HorizontalContentAlignment=HorizontalAlignment.Stretch};
                ToolTipService.SetToolTip(item,profile.Address+" · "+profile.Mode+(profile.Mode=="Icmp"?"":$" · TCP {profile.Port}"));
                TargetList.Items.Add(item);if(profile.Id==_selectedProfileId)TargetList.SelectedItem=item;
            }
        }
        finally{_rebuildingList=false;}
        UpdateLiveStates();
    }
    private void TargetSearch_Changed(object sender,TextChangedEventArgs e){if(_manager is not null)RebuildTargetList();}
    private async void TargetList_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_rebuildingList||(TargetList.SelectedItem as ListViewItem)?.Tag is not string id)return;
        var profile=_settings.Profiles.FirstOrDefault(p=>p.Id==id);if(profile is null)return;
        SelectProfile(profile);try{_settings.Save();}catch(Exception ex){ShowError(ex);}await RefreshAsync();
    }
    private void SetWorkspace(int view)
    {
        if(_workspaceView!=view){_selectionVersion++;_allTargetsDirty=_comparisonDirty=true;}
        _workspaceView=view;
        AllTargetsPanel.Visibility=view==0?Visibility.Visible:Visibility.Collapsed;
        TargetDetailPanel.Visibility=view==1?Visibility.Visible:Visibility.Collapsed;
        ComparePanel.Visibility=view==2?Visibility.Visible:Visibility.Collapsed;
        UnifiedSettingsPanel.Visibility=view==3?Visibility.Visible:Visibility.Collapsed;
        PeriodBox.Visibility=view==3?Visibility.Collapsed:Visibility.Visible;
        GlobalProtocolBox.Visibility=view is 0 or 2?Visibility.Visible:Visibility.Collapsed;
        WorkspaceTitle.Text=view==3?"统一设置":view==0?"全部目标":view==2?"时段对比":_settings.Profiles.FirstOrDefault(p=>p.Id==_selectedProfileId)?.DisplayName??"目标详情";
        ExportButton.IsEnabled=view==1&&_target is not null;DashboardScroll.ChangeView(null,0,null,true);
        if(view!=1)CancelMetadata();else Chart.InvalidatePlot();
        RenderVisibleMulti();
    }
    private async void AllTargets_Click(object sender,RoutedEventArgs e){SetWorkspace(0);await RefreshAsync();}
    private void AddTarget_Click(object sender,RoutedEventArgs e)
    {
        _selectedProfileId=null;_viewRevision=null;_selectionVersion++;_target=_primaryTarget=_secondaryTarget=null;
        _cancellation?.Dispose();_cancellation=null;ApplyEditor(new TargetProfile());SetRunning(false);
        TargetEditor.IsExpanded=true;InputError.Visibility=Visibility.Collapsed;TargetText.Text="填写新目标后保存或开始监控";SetWorkspace(0);NameBox.Focus(FocusState.Programmatic);
    }
    private async void RemoveTarget_Click(object sender,RoutedEventArgs e)
    {
        var profile=_settings.Profiles.FirstOrDefault(p=>p.Id==_selectedProfileId);if(profile is null||_manager is null)return;
        try
        {
            await _manager.StopAsync(profile.Id);_settings.Profiles.Remove(profile);_settings.ArchivedProfiles.Add(profile);_compared.Remove(profile.Id);
            var next=_settings.Profiles.FirstOrDefault();if(next is not null)SelectProfile(next);else{_selectedProfileId=null;_target=_primaryTarget=_secondaryTarget=null;ApplyEditor(new TargetProfile());SetRunning(false);}
            _settings.SelectedProfileId=_selectedProfileId??"";_settings.Save();RebuildTargetList();_multiLoaded=DateTimeOffset.MinValue;SetWorkspace(0);await RefreshAsync();
            StatusText.Text="目标已移除，历史保留；可通过“恢复已移除目标”找回。";
        }
        catch(Exception ex){ShowError(ex);}
    }
    private async void RestoreTarget_Click(object sender,RoutedEventArgs e)
    {
        if(_settings.ArchivedProfiles.Count==0){StatusText.Text="没有已移除的目标。";return;}
        var choices=new ComboBox{HorizontalAlignment=HorizontalAlignment.Stretch};
        foreach(var profile in _settings.ArchivedProfiles)choices.Items.Add(new ComboBoxItem{Content=profile.DisplayName+" · "+profile.Address,Tag=profile.Id});choices.SelectedIndex=0;
        var dialog=new ContentDialog{XamlRoot=RootGrid.XamlRoot,Title="恢复已移除的目标",Content=choices,PrimaryButtonText="恢复",CloseButtonText="取消",DefaultButton=ContentDialogButton.Primary};
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
        if((choices.SelectedItem as ComboBoxItem)?.Tag is not string id)return;
        var restored=_settings.ArchivedProfiles.Single(p=>p.Id==id);_settings.ArchivedProfiles.Remove(restored);restored.ResumeOnLaunch=false;_settings.GlobalMonitoring.Apply(restored);_settings.Profiles.Add(restored);
        SelectProfile(restored);try{_settings.Save();}catch(Exception ex){ShowError(ex);}RebuildTargetList();_multiLoaded=DateTimeOffset.MinValue;await RefreshAsync();
    }
    private async void StartAll_Click(object sender,RoutedEventArgs e)=>await StartAllAsync();
    private async Task StartAllAsync()
    {
        if(_manager is null||_storageFailed||_stopping)return;
        var errors=new List<string>();
        foreach(var profile in _settings.Profiles.ToArray())try{await _manager.StartAsync(profile);}catch(Exception ex){errors.Add(profile.DisplayName+"："+ex.Message);}
        UpdateLiveStates();_multiLoaded=DateTimeOffset.MinValue;await RefreshAsync();
        StatusText.Text=errors.Count>0?string.Join("；",errors):$"正在监测 {_manager.RunningProfiles} 个目标";
        if(errors.Count>0){InputError.Text=string.Join("\n",errors);InputError.Visibility=Visibility.Visible;TargetEditor.IsExpanded=true;}
    }
    private async void StopAll_Click(object sender,RoutedEventArgs e)=>await PauseAllAsync();
    private async Task PauseAllAsync()
    {
        if(_manager is null||_stopping)return;StopAllButton.IsEnabled=false;
        try{await _manager.StopAllAsync();}finally{UpdateLiveStates();_multiLoaded=DateTimeOffset.MinValue;}
        StatusText.Text="全部目标已暂停 · 已接受的记录均已写入";await RefreshAsync();
    }
    private async Task ToggleTargetAsync(TargetProfile profile)
    {
        if(_manager is null||_stopping)return;
        try{if(_manager.IsRunning(profile.Id))await _manager.StopAsync(profile.Id);else await _manager.StartAsync(profile);}
        catch(Exception ex){ShowError(ex);TargetEditor.IsExpanded=true;}
        UpdateLiveStates();_multiLoaded=DateTimeOffset.MinValue;await RefreshAsync();
    }
    private void UpdateLiveStates()
    {
        if(_manager is null)return;Interlocked.Exchange(ref _liveDirty,0);
        bool selectedRunning=_selectedProfileId is not null&&_manager.IsRunning(_selectedProfileId);
        if(selectedRunning)_cancellation??=new();else{_cancellation?.Dispose();_cancellation=null;}
        foreach(var target in new[]{_primaryTarget,_secondaryTarget}.OfType<Target>())
        {
            if(_manager.State(target) is not MeasurementState live)continue;
            var session=new Session{Last=live.Last,Success=live.Success,Failure=live.Failure,Error=live.LocalErrors,Consecutive=live.Consecutive,FirstFailure=live.FirstFailure};_sessions[target.Key]=session;
            if(_target?.Key==target.Key&&_viewRevision is null)
            {
                if(!_hidden)DisplaySession(session);
                else{_sessionSuccess=live.Success;_sessionFailure=live.Failure;_sessionError=live.LocalErrors;_lastSample=live.Last;}
            }
        }
        if(_hidden)return;
        SetRunning(selectedRunning);SetEnabled(StartAllButton,!_storageFailed&&_settings.Profiles.Any(p=>!_manager.IsRunning(p.Id)));SetEnabled(StopAllButton,_manager.RunningProfiles>0);
        SetText(RunState,selectedRunning?"正在监控":_selectedProfileId is null?"尚未选择目标":"已暂停");
        if(_primaryTarget is not null&&_manager.State(_primaryTarget) is MeasurementState primary)
        {RouteStatus.Text=primary.RouteState;ContextText.Text=primary.Context;}
        foreach(var profile in _settings.Profiles)
        {
            bool running=_manager.IsRunning(profile.Id);var state=_manager.State(profile.Primary);
            if(_listStates.TryGetValue(profile.Id,out var label))label.Text=(running?"● ":"○ ")+profile.Address+" · "+(running?state?.Health??"等待采样":"暂停");
            var displayTarget=profile.ForProtocol(GlobalProtocolBox.SelectedIndex==1?ProbeProtocol.Tcp:ProbeProtocol.Icmp);
            var live=displayTarget is null?null:_manager.State(displayTarget);
            if(_workspaceView!=0)continue;
            if(_rowStates.TryGetValue(profile.Id,out var row))
            {
                SetText(row,displayTarget is null?"未启用该协议":!running?"已暂停":live?.Health??"等待采样");
                var foreground=running&&live?.Health is "持续失败" or "延迟升高"?_warningBrush:SecondaryBrush;if(!ReferenceEquals(row.Foreground,foreground))row.Foreground=foreground;
                SetTip(row,live is null?row.Text:$"{row.Text}\n本次成功 {live.Success} / 失败 {live.Failure} / 本地错误 {live.LocalErrors}\n本机繁忙跳过 {live.Skipped} 次 · 未计为目标失败\n{live.RouteState}");
            }
            if(_rowLatest.TryGetValue(profile.Id,out var latest)&&displayTarget is not null)
            {
                var saved=_multiOverview?.Targets.FirstOrDefault(t=>t.ProfileId==profile.Id);
                var sample=live?.Last??saved?.Latest;SetText(latest,(sample?.LatencyMs is double ms?(ms<1?"<1 ms":$"{ms:0.#} ms"):sample is null?"—":Sample.Label(sample.Status))+"\nP95 "+(saved?.P95Hour is double p?$"{p:0.#}":"—"));
                SetTip(latest,sample is null?"暂无采样":$"最近一次 {sample.Time.ToLocalTime():MM-dd HH:mm:ss}\n{sample.Detail}\nP95 为近 1 小时成功样本分位数");
            }
            if(_rowActions.TryGetValue(profile.Id,out var action)){SetContent(action,running?"暂停":"开始");SetEnabled(action,!_storageFailed);}
        }
        SetText(AllTargetsInfo,$"{_settings.Profiles.Count} 个目标 · {_manager.RunningProfiles} 个运行 · 路由排队 {_manager.PendingRoutes}");
    }
    private async void GlobalProtocol_Changed(object sender,SelectionChangedEventArgs e)
    {if(_manager is null)return;_overviewRead?.Cancel();_multiLoaded=DateTimeOffset.MinValue;await RefreshAsync();}
    private async void Compare_Click(object sender,RoutedEventArgs e)
    {
        if(_compared.Count==0){StatusText.Text="先勾选需要对比的目标，最多 4 个。";return;}
        SetWorkspace(2);RenderComparison();await RefreshAsync();
    }
    private void CompareMetric_Changed(object sender,SelectionChangedEventArgs e){if(_manager is not null)RenderComparison();}
    private async Task RefreshMultiAsync()
    {
        if(_hidden)return;int version=_selectionVersion;var now=DateTimeOffset.UtcNow;var profiles=_settings.Profiles.Select(p=>p.Copy()).ToArray();
        var protocol=GlobalProtocolBox.SelectedIndex==1?ProbeProtocol.Tcp:ProbeProtocol.Icmp;int days=_timelineDays;
        string key=protocol+"|"+days+"|"+string.Join(";",profiles.Select(p=>p.Id+"|"+p.Primary.Key+"|"+p.Secondary?.Key));
        if(_multiKey==key&&now-_multiLoaded<TimeSpan.FromSeconds(15)){RenderVisibleMulti();UpdateLiveStates();return;}
        _overviewRead?.Cancel();_overviewRead?.Dispose();_overviewRead=new();var token=_overviewRead.Token;
        try
        {
            var overview=await Task.Run(()=>_history.LoadOverview(profiles,protocol,now,days,token),token);
            if(token.IsCancellationRequested||version!=_selectionVersion||days!=_timelineDays||protocol!=(GlobalProtocolBox.SelectedIndex==1?ProbeProtocol.Tcp:ProbeProtocol.Icmp))return;
            _multiOverview=overview;_multiLoaded=now;_multiKey=key;_allTargetsDirty=_comparisonDirty=true;RenderVisibleMulti();UpdateLiveStates();
        }
        catch(OperationCanceledException){}
        catch(Exception ex){StatusText.Text="总览读取失败："+ex.Message;StartupDiagnostics.Write("Overview read failed.",ex);}
    }
    private static TextBlock CompactText(string text,int size=12)=>new(){Text=text,FontSize=size,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
    private void RenderAllTargets()
    {
        _allTargetsDirty=true;if(_hidden||_workspaceView!=0||_multiOverview is null)return;_allTargetsDirty=false;
        var grid=AllTargetsGrid;
        var ids=_settings.Profiles.Select(p=>p.Id).ToHashSet();
        foreach(var id in _overviewRows.Keys.Where(id=>!ids.Contains(id)).ToArray())
        {foreach(var cell in _overviewRows[id].Cells)grid.Children.Remove(cell);_overviewRows.Remove(id);_rowStates.Remove(id);_rowLatest.Remove(id);_rowActions.Remove(id);}
        _compared.RemoveWhere(id=>!ids.Contains(id));
        if(grid.ColumnDefinitions.Count==0)
        {
        foreach(double width in new[]{26d,140,85,94,95,82,210,52})grid.ColumnDefinitions.Add(new(){Width=new GridLength(width)});
        grid.RowDefinitions.Add(new(){Height=new GridLength(32)});
        string[] headers={"","目标","当前状态","最新 / P95(1h)","时段失败 / 样本","趋势","最新城市 / 线路",""};
        void Cell(FrameworkElement element,int row,int col){Grid.SetRow(element,row);Grid.SetColumn(element,col);grid.Children.Add(element);}
        for(int c=0;c<headers.Length;c++){var text=CompactText(headers[c],10);_overviewHeaders.Add(text);Cell(text,0,c);}
        }
        foreach(var header in _overviewHeaders)if(!ReferenceEquals(header.Foreground,SecondaryBrush))header.Foreground=SecondaryBrush;
        int index=0;
        foreach(var entry in _multiOverview.Targets)
        {
            var profile=_settings.Profiles.FirstOrDefault(p=>p.Id==entry.ProfileId);if(profile is null)continue;int row=++index;
            if(grid.RowDefinitions.Count<=row)grid.RowDefinitions.Add(new(){Height=new GridLength(86)});
            if(!_overviewRows.TryGetValue(profile.Id,out var cached))
            {
            string profileId=profile.Id;
            void Cell(FrameworkElement element,int r,int c){Grid.SetRow(element,r);Grid.SetColumn(element,c);grid.Children.Add(element);}
            var select=new CheckBox{IsChecked=_compared.Contains(profileId),MinWidth=24,VerticalAlignment=VerticalAlignment.Center};
            select.Click+=(_,_)=>
            {
                if(select.IsChecked==true){if(_compared.Count>=4&&!_compared.Contains(profileId)){select.IsChecked=false;StatusText.Text="同时最多对比 4 个目标。";return;}_compared.Add(profileId);}else _compared.Remove(profileId);
                _comparisonDirty=true;
                SetContent(CompareButton,$"对比所选（{_compared.Count}/4）");
            };ToolTipService.SetToolTip(select,"加入时段对比");Cell(select,row,0);
            var name=new StackPanel{Spacing=3};name.Children.Add(CompactText(profile.DisplayName,13));name.Children.Add(new TextBlock{Text=profile.Address+(entry.Target?.Protocol==ProbeProtocol.Tcp?$" :{profile.Port}":""),FontSize=10,TextTrimming=TextTrimming.CharacterEllipsis,Foreground=SecondaryBrush});
            var open=new Button{Content=name,HorizontalContentAlignment=HorizontalAlignment.Stretch,HorizontalAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(3),BorderThickness=new Thickness(0),Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent)};
            open.Click+=async(_,_)=>{var current=_settings.Profiles.FirstOrDefault(p=>p.Id==profileId);if(current is null)return;SelectProfile(current);RebuildTargetList();await RefreshAsync();};ToolTipService.SetToolTip(open,profile.DisplayName+"\n"+profile.Address);Cell(open,row,1);
            var state=CompactText("等待采样",11);state.TextWrapping=TextWrapping.Wrap;_rowStates[profile.Id]=state;Cell(state,row,2);
            var latest=CompactText("—",11);_rowLatest[profile.Id]=latest;Cell(latest,row,3);
            var loss=CompactText("",11);Cell(loss,row,4);
            var trend=new MultiLatencyChart{Height=50,Mini=true};Cell(trend,row,5);
            var route=CompactText("",10);route.MaxLines=3;route.TextWrapping=TextWrapping.Wrap;Cell(route,row,6);
            var action=new Button{Content="开始",FontSize=11,Padding=new Thickness(7,4,7,4),VerticalAlignment=VerticalAlignment.Center};
            action.Click+=async(_,_)=>{var current=_settings.Profiles.FirstOrDefault(p=>p.Id==profileId);if(current is null)return;action.IsEnabled=false;await ToggleTargetAsync(current);};
            _rowActions[profileId]=action;Cell(action,row,7);
            cached=new OverviewRow{Select=select,Open=open,Action=action,Name=(TextBlock)name.Children[0],Address=(TextBlock)name.Children[1],State=state,Latest=latest,Loss=loss,Trend=trend,Route=route};
            _overviewRows[profileId]=cached;
            }
            foreach(var cell in cached.Cells)if(Grid.GetRow(cell)!=row)Grid.SetRow(cell,row);
            bool selected=_compared.Contains(profile.Id);if(cached.Select.IsChecked!=selected)cached.Select.IsChecked=selected;
            SetText(cached.Name,profile.DisplayName);
            SetText(cached.Address,profile.Address+(entry.Target?.Protocol==ProbeProtocol.Tcp?$" :{profile.Port}":""));
            if(!ReferenceEquals(cached.Address.Foreground,SecondaryBrush))cached.Address.Foreground=SecondaryBrush;
            SetTip(cached.Open,profile.DisplayName+"\n"+profile.Address);
            var total=entry.Timeline?.Total;SetText(cached.Loss,total?.Attempts>0?$"{100-total.Availability:0.0}%\n{total.Attempts} 次":"—\n暂无有效样本");
            SetTip(cached.Loss,total is null?"该协议未启用":$"所选时段：成功 {total.Successes} / 失败 {total.Attempts-total.Successes} / 本地错误 {total.LocalErrors}\n暂停或本机繁忙时不补记失败；比例仅针对有效尝试。");
            cached.Trend.SetSeries(entry.Timeline is Timeline data?new[]{new ChartSeries(profile.DisplayName,data,MultiLatencyChart.Palette[0])}:Array.Empty<ChartSeries>());
            if(entry.Target is null)cached.Latest.Text="—";
            string routeText="暂无路由";
            if(entry.Route is RouteRun run)
            {
                var annotation=entry.Annotation??RouteClassifier.Classify(run,new Dictionary<string,NodeMetadata>(),now:DateTimeOffset.UtcNow,origin:"本地预览");
                var nodes=RouteTable.Build(run,annotation).Rows.SelectMany(r=>r.Nodes).ToArray();
                var cities=new List<string>();foreach(var node in nodes){var city=node.Location;if(node.Annotation?.Identity?.Location.Level!="city")continue;if(cities.LastOrDefault()!=city)cities.Add(city);}
                var networks=nodes.Where(n=>!n.Target).Select(n=>n.Network).Distinct().ToArray();
                routeText=(cities.Count==0?"城市未识别":string.Join(" → ",cities))+"\n"+(networks.Length==0?"线路未识别":string.Join(" · ",networks))+ $"\n{run.Started.ToLocalTime():MM-dd HH:mm} · {(run.Reached?"到达目标":"部分路径")}";
            }
            SetText(cached.Route,routeText);SetTip(cached.Route,routeText+"\n地区为 IP 定位；仅表示可见节点，不认证 GIA / GT。");
            
        }
        SetContent(CompareButton,$"对比所选（{_compared.Count}/4）");
        while(grid.RowDefinitions.Count>index+1)grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count-1);
        SetText(OverviewAsOf,$"时段统计截至 {_multiOverview.AsOf.ToLocalTime():MM-dd HH:mm:ss} · {(_multiOverview.Days==1?"近 24 小时":"近 7 天")} · {_multiOverview.Protocol} · 每 15 秒刷新统计，当前状态每秒刷新。P95 固定取近 1 小时。");
    }
    private readonly Dictionary<string,ComparisonRow> _comparisonRows=new();
    private sealed class ComparisonRow
    {
        public required StackPanel Panel;public required TextBlock Title,Hint,Legend;
        public required Grid Grid;public required SolidColorBrush Shade;public required HourMatrix Matrix;
    }
    private void RenderComparison()
    {
        _comparisonDirty=true;if(_hidden||_workspaceView!=2||_multiOverview is null||CompareChart is null)return;_comparisonDirty=false;
        var entries=_multiOverview.Targets.Where(t=>_compared.Contains(t.ProfileId)&&_settings.Profiles.Any(p=>p.Id==t.ProfileId)).Take(4).ToArray();
        var ids=entries.Select(t=>t.ProfileId).ToHashSet();
        foreach(var id in _comparisonRows.Keys.Where(id=>!ids.Contains(id)).ToArray())
        {var removed=_comparisonRows[id];CompareHours.Children.Remove(removed.Panel);CompareLegend.Children.Remove(removed.Legend);_comparisonRows.Remove(id);}
        var series=new List<ChartSeries>();int color=0;
        foreach(var entry in entries)
        {
            var profile=_settings.Profiles.FirstOrDefault(p=>p.Id==entry.ProfileId);if(profile is null)continue;
            var shade=MultiLatencyChart.Palette[color++];string label=profile.DisplayName+(entry.Target?.Protocol==ProbeProtocol.Tcp?$" :{profile.Port}":"");
            if(!_comparisonRows.TryGetValue(profile.Id,out var row))
            {
                var panel=new StackPanel{Spacing=5};var title=CompactText("",12);var hint=CompactText("未启用该协议，不按故障统计。",11);
                var grid=new Grid{ColumnSpacing=3,RowSpacing=3};panel.Children.Add(title);panel.Children.Add(hint);panel.Children.Add(grid);
                row=new(){Panel=panel,Title=title,Hint=hint,Grid=grid,Legend=CompactText("",11),Shade=new SolidColorBrush(shade),Matrix=new HourMatrix(grid,(from,until,description)=>{CompareChart.SelectInterval(from,until);CompareInfo.Text=description.Replace('\n',' ');})};
                _comparisonRows[profile.Id]=row;row.Legend.Foreground=row.Shade;
            }
            SetText(row.Legend,"● "+label);if(row.Shade.Color!=shade)row.Shade.Color=shade;
            int position=color-1;
            if(CompareLegend.Children.IndexOf(row.Legend)!=position){CompareLegend.Children.Remove(row.Legend);CompareLegend.Children.Insert(position,row.Legend);}
            if(CompareHours.Children.IndexOf(row.Panel)!=position){CompareHours.Children.Remove(row.Panel);CompareHours.Children.Insert(position,row.Panel);}
            if(entry.Timeline is Timeline data)series.Add(new(label,data,shade));
        }
        CompareChart.SetSeries(series);
        SetText(CompareInfo,$"{entries.Length} 个目标 · {_multiOverview.Protocol} · 共用时间轴与延迟刻度；未采样处断开。");
        SetText(CompareRange,series.Count==0?"所选目标未启用该协议。":$"{series[0].Timeline.From.ToLocalTime():MM-dd HH:mm} → {_multiOverview.AsOf.ToLocalTime():MM-dd HH:mm} · 每点 {series[0].Timeline.StepMinutes} 分钟");
        var values=series.SelectMany(s=>s.Timeline.Hours).Where(b=>b.Average.HasValue).Select(b=>b.Average!.Value).ToArray();double min=values.Length==0?0:values.Min(),max=values.Length==0?0:values.Max();bool loss=CompareMetricBox.SelectedIndex==1;
        foreach(var entry in entries)
        {
            var profile=_settings.Profiles.FirstOrDefault(p=>p.Id==entry.ProfileId);if(profile is null)continue;
            var row=_comparisonRows[profile.Id];
            SetText(row.Title,profile.DisplayName+$" · {(entry.Target is null?"未启用该协议":entry.Target.Protocol==ProbeProtocol.Tcp?$"TCP {profile.Port}":"ICMP")} · 间隔 {profile.IntervalSeconds}s / 超时 {profile.TimeoutMilliseconds}ms");
            bool enabled=entry.Timeline is not null;row.Hint.Visibility=enabled?Visibility.Collapsed:Visibility.Visible;row.Grid.Visibility=enabled?Visibility.Visible:Visibility.Collapsed;
            if(entry.Timeline is Timeline timeline)row.Matrix.Update(timeline,loss,min,max,profile.DisplayName);else row.Matrix.Clear();
        }
        SetText(CompareHeatLegend,(loss?"共用颜色：失败比例 0% → 100%":$"共用颜色：小时均值 {min:0.#} → {max:0.#} ms")+" · 灰色未采样 · × 全部失败 · 点号含失败 · ! 仅本地错误。颜色用于比较数值，不能直接判断远距离目标异常。");
    }
}
