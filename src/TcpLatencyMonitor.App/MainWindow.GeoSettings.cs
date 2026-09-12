using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TcpLatencyMonitor.App.Services;
using TcpLatencyMonitor.Core;

namespace TcpLatencyMonitor.App;

public sealed partial class MainWindow
{
    private string _geoWarning="";
    private bool _geoConfigurationValid=true;
    private Func<ContentDialog,Task>? _geoTestAction;
    private async Task<ContentDialogResult> ShowGeoDialogAsync(ContentDialog dialog)
    {
        var pending=dialog.ShowAsync();
        if(_geoTestAction is {} action){await Task.Delay(400);await action(dialog);}
        return await pending;
    }
    private void InitializeGeoSettings()
    {
        try{_metadataClient!.Configure(new(_settings.GeoPrimary,_settings.GeoCrossCheck,GeoTokenStore.Read(),GeoTokenStore.Read("nexttrace"),_settings.GeoRefreshHours));}
        catch(Exception){_geoWarning="定位配置暂不可用，在线注释已暂停；请打开定位数据源检查。已保存注释仍可查看。";_geoConfigurationValid=false;}
        UpdateGeoNotice();
    }
    private void UpdateGeoNotice()
    {
        var options=_metadataClient?.Options??new();
        AnnotationQueryNotice.Text=$"公网节点查询 {(options.Primary=="nexttrace"?"NextTrace（v4 优先，v3 PoW 备用）":options.Primary)}；数据库共享缓存 {options.RefreshHours} 小时。仅最近 24 小时路由中出现的节点自动刷新。"+_geoWarning;
    }
    private async void GeoSettings_Click(object sender,RoutedEventArgs e)=>await ShowGeoSettingsAsync();
    private async Task ShowGeoSettingsAsync()
    {
        try
        {
            var primary=new ComboBox{Header="首选数据源",ItemsSource=new[]{"ipwho.is（基础）","IPinfo Core（需城市接口权限）","NextTrace（v4 优先 / v3 PoW 备用）"},SelectedIndex=_settings.GeoPrimary switch{"ipinfo-core"=>1,"nexttrace"=>2,_=>0},HorizontalAlignment=HorizontalAlignment.Stretch};
            var cross=new CheckBox{Content="双源复核（NextTrace / IPinfo 可与 ipwho.is 核对）",IsChecked=_settings.GeoCrossCheck};
            var hours=new NumberBox{Header="缓存刷新间隔（小时）",Value=_settings.GeoRefreshHours,Minimum=1,Maximum=168,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact};
            var ipinfo=new PasswordBox{Name="IpinfoTokenInput",Header="IPinfo Token",PlaceholderText="留空沿用已保存 Token"};
            var removeIpinfo=new CheckBox{Content="移除已保存的 IPinfo Token"};
            var nexttrace=new PasswordBox{Name="NextTraceTokenInput",Header="NextTrace v4 七天令牌（可选）",PlaceholderText="粘贴新领取的令牌；留空沿用已保存令牌"};
            var removeNextTrace=new CheckBox{Content="移除已保存的 NextTrace v4 令牌"};
            var status=new TextBlock{Text=_metadataClient!.DescribeNextTraceToken(),TextWrapping=TextWrapping.Wrap,FontSize=12};
            var claim=new Button{Content="领取 / 刷新 v4 七天令牌 ↗",HorizontalAlignment=HorizontalAlignment.Left};
            claim.Click+=async (_,_)=>
            {
                try{if(!await Windows.System.Launcher.LaunchUriAsync(new Uri(NodeMetadataClient.NextTraceTokenPage)))status.Text="未能打开浏览器，请访问 "+NodeMetadataClient.NextTraceTokenPage;}
                catch(Exception){status.Text="未能打开浏览器，请访问 "+NodeMetadataClient.NextTraceTokenPage;}
            };
            var error=new TextBlock{TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Microsoft.UI.Colors.OrangeRed)};
            var content=new StackPanel{Spacing=10,MinWidth=420};content.Children.Add(primary);content.Children.Add(hours);content.Children.Add(cross);
            content.Children.Add(nexttrace);content.Children.Add(status);content.Children.Add(claim);content.Children.Add(removeNextTrace);
            content.Children.Add(new TextBlock{Text="网页领取后，将令牌粘贴到上方并保存。有效期以服务端返回为准；未配置、已过期或失效时使用 v3 PoW。遇服务限流会暂停并保留缓存。",TextWrapping=TextWrapping.Wrap,FontSize=12});
            content.Children.Add(ipinfo);content.Children.Add(removeIpinfo);
            content.Children.Add(new TextBlock{Text="新节点立即排队查询；缓存到期后，仅自动刷新最近 24 小时路由中出现的 IP。私网不提交。Token 使用当前 Windows 用户加密保存，不进入导出包。",TextWrapping=TextWrapping.Wrap,FontSize=12});
            content.Children.Add(error);
            var dialog=new ContentDialog{XamlRoot=RootGrid.XamlRoot,Title="定位数据源",Content=new ScrollViewer{MaxHeight=600,Content=content},PrimaryButtonText="保存",CloseButtonText="取消"};
            MetadataOptions? chosen=null;
            dialog.PrimaryButtonClick+=(_,args)=>
            {
                try
                {
                    string value=removeIpinfo.IsChecked==true?"":ipinfo.Password.Length>0?ipinfo.Password.Trim():GeoTokenStore.Read();
                    string ntValue=removeNextTrace.IsChecked==true?"":nexttrace.Password.Length>0?nexttrace.Password.Trim():GeoTokenStore.Read("nexttrace");
                    string provider=primary.SelectedIndex switch{1=>"ipinfo-core",2=>"nexttrace",_=>"ipwho.is"};
                    if((provider=="ipinfo-core"||provider=="ipwho.is"&&cross.IsChecked==true)&&value.Length==0)throw new ArgumentException("当前数据源 / 双源复核需要 IPinfo Token。");
                    if(value.Length>1024||ntValue.Length>4096||value.Any(char.IsControl)||ntValue.Any(char.IsControl))throw new ArgumentException("Token 格式无效。");
                    if(!double.IsFinite(hours.Value)||hours.Value<1||hours.Value>168||hours.Value!=Math.Truncate(hours.Value))throw new ArgumentException("刷新间隔须为 1–168 的整数小时。");
                    var options=new MetadataOptions(provider,cross.IsChecked==true,value,ntValue,(int)hours.Value);
                    GeoTokenStore.Save(value);GeoTokenStore.Save(ntValue,"nexttrace");
                    _settings.GeoPrimary=provider;_settings.GeoCrossCheck=options.CrossCheck;_settings.GeoRefreshHours=options.RefreshHours;_settings.Save();chosen=options;
                }
                catch(Exception ex){error.Text=ex.Message;args.Cancel=true;}
            };
            if(await ShowGeoDialogAsync(dialog)!=ContentDialogResult.Primary||chosen is null)return;
            _annotationService?.CancelRequests();_metadataClient.Configure(chosen);_geoConfigurationValid=true;_geoWarning="";
            await Task.Run(()=>_history.ResetProviderFailures("ipinfo-core"));
            if(_annotationService is not null)_annotationService.Mode=!_settings.EnableNodeMetadata?0:_settings.BackgroundNodeMetadata?2:1;
            UpdateGeoNotice();_lastViewAttemptId=null;await RefreshGeoEvidenceAsync();
        }
        catch(Exception ex){ShowError(ex);}
    }
    private async void GeoNodeSelect_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var run=_displayedRoute??_overviewTableRun;if(run is null)return;
            var ips=run.Probes.Where(p=>p.Address is not null&&NodeMetadataClient.LocalLabel(p.Address) is null).Select(p=>CidrBlock.Normalize(p.Address!)).Distinct().ToArray();
            if(ips.Length==0)throw new InvalidOperationException("此路由暂无可校准的公网节点。");
            var select=new ComboBox{ItemsSource=ips,SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch};
            var dialog=new ContentDialog{XamlRoot=RootGrid.XamlRoot,Title="选择节点",Content=select,PrimaryButtonText="查看 / 校准",CloseButtonText="取消"};
            if(await ShowGeoDialogAsync(dialog)==ContentDialogResult.Primary)await ShowNodeCalibrationAsync((string)select.SelectedItem);
        }
        catch(Exception ex){ShowError(ex);}
    }
    private async Task ShowNodeCalibrationAsync(string address,NodeMetadata? snapshot=null)
    {
        try
        {
            var data=await Task.Run(()=>_history.LoadNodeMetadata(address));var old=await Task.Run(()=>_history.LoadCalibration(address));
            var country=new TextBox{Header="国家 / 地区",Text=old?.Country??data?.Country??"",MaxLength=160};
            var region=new TextBox{Header="省 / 州（可选）",Text=old?.Region??data?.Region??"",MaxLength=160};
            var city=new TextBox{Header="城市（可选）",Text=old?.City??data?.City??"",MaxLength=160};
            var note=new TextBox{Header="校准依据（必填）",Text=old?.Note??"",MaxLength=1000};
            var days=new NumberBox{Header="复核期限（天）",Value=7,Minimum=1,Maximum=365,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact};
            var error=new TextBlock{TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Microsoft.UI.Colors.OrangeRed)};
            var panel=new StackPanel{Spacing=8,MinWidth=440};
            var location=NodeClassifier.Locate(data,DateTimeOffset.UtcNow);
            panel.Children.Add(new TextBlock{Text=$"当前：{location.Label} · {location.Marker}",TextWrapping=TextWrapping.Wrap,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            panel.Children.Add(new ScrollViewer{MaxHeight=150,Content=new TextBlock{Text=location.Details.Length>0?location.Details:"尚无在线注释。",TextWrapping=TextWrapping.Wrap,FontSize=11,IsTextSelectionEnabled=true}});
            if(snapshot is not null)panel.Children.Add(new ScrollViewer{MaxHeight=110,Content=new TextBlock{Text="所点路由保存的依据：\n"+NodeClassifier.Locate(snapshot,DateTimeOffset.UtcNow).Details+"\n下方编辑当前全局校准；原始快照保留。",FontSize=11,TextWrapping=TextWrapping.Wrap}});
            panel.Children.Add(country);panel.Children.Add(region);panel.Children.Add(city);panel.Children.Add(note);panel.Children.Add(days);panel.Children.Add(error);
            var dialog=new ContentDialog{XamlRoot=RootGrid.XamlRoot,Title="节点依据 / 校准 · "+address,Content=new ScrollViewer{MaxHeight=640,Content=panel},
                PrimaryButtonText="保存校准",SecondaryButtonText=old is null?"":"撤销校准",CloseButtonText="关闭"};
            dialog.PrimaryButtonClick+=(_,args)=>{if(string.IsNullOrWhiteSpace(country.Text)||string.IsNullOrWhiteSpace(note.Text)||!double.IsFinite(days.Value)||days.Value<1||days.Value>365){error.Text="请填写国家、依据，以及 1–365 天的复核期限。";args.Cancel=true;}};
            var result=await ShowGeoDialogAsync(dialog);var now=DateTimeOffset.UtcNow;
            if(result==ContentDialogResult.Primary)
            {
                var calibration=new NodeCalibration(address,country.Text.Trim(),region.Text.Trim(),city.Text.Trim(),note.Text.Trim(),now,now.AddDays(days.Value));
                await Task.Run(()=>_history.SetCalibration(calibration));
            }
            else if(result==ContentDialogResult.Secondary)await Task.Run(()=>_history.RemoveCalibration(address));
            else return;
            await RefreshGeoEvidenceAsync();
        }
        catch(Exception ex){ShowError(ex);}
    }
    private async Task RefreshGeoEvidenceAsync()
    {
        var runs=new List<RouteRun>();if(_displayedRoute is not null)runs.Add(_displayedRoute);if(_overviewTableRun is not null)runs.Add(_overviewTableRun);
        foreach(var profile in _settings.Profiles)runs.AddRange(await Task.Run(()=>_history.LoadRoutes(profile.Primary,1)));
        foreach(var run in runs.DistinctBy(r=>r.Id))await Task.Run(()=>_history.ReinterpretWithCurrentEvidence(run));
        _overviewRouteDirty=true;_recordsDirty=true;_multiLoaded=DateTimeOffset.MinValue;
        if(!_hidden&&_workspaceView==1&&_displayedRoute is not null){OriginalAnnotationBox.IsChecked=false;RenderRoute(_displayedRoute);}await RefreshAsync();
    }
}
