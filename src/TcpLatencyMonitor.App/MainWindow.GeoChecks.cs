using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TcpLatencyMonitor.App.Services;
using TcpLatencyMonitor.Core;

namespace TcpLatencyMonitor.App;

public sealed partial class MainWindow
{
    private async Task VerifyNativeV4Async()
    {
        if(_settings.Profiles.Count!=0||_settings.GeoPrimary!="nexttrace"||_metadataClient is null)throw new InvalidOperationException("Live v4 self-test requires an isolated empty profile list");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result=await _metadataClient.GetAsync("202.97.63.30",true,timeout.Token);
        if(!result.Success||result.QueryState!="NextTrace v4"||result.City.Length==0)throw new InvalidOperationException("Native v4 verification did not return a v4 success");
        var cached=await _metadataClient.GetAsync(result.Address,true,timeout.Token);
        if(cached.Queried!=result.Queried)throw new InvalidOperationException("Native v4 cache mismatch");
        ApplyGlobalEditor();SetWorkspace(3);
        _geoTestAction=async dialog=>{await CaptureForVerificationAsync("-v4-live",dialog);dialog.Hide();};
        await ShowGeoSettingsAsync();_geoTestAction=null;
        File.WriteAllText(Path.Combine(AppPaths.DataDirectory,"v4-live-result.txt"),$"PASS NativeAOT + encrypted token + bundled helper: {result.Address} {result.Country}/{result.Region}/{result.City} AS{result.Asn}; {result.QueryState}; {_metadataClient.DescribeNextTraceToken()}; database cache reused.");
    }
    private static IEnumerable<DependencyObject> GeoVisuals(DependencyObject root)
    {
        yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var item in GeoVisuals(VisualTreeHelper.GetChild(root,i)))yield return item;
    }
    private static void InvokeGeoButton(ContentDialog dialog,string caption)
    {
        var button=GeoVisuals(dialog).OfType<Button>().First(b=>b.Content?.ToString()==caption);
        var peer=new ButtonAutomationPeer(button);((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private async Task VerifyGeoUiAsync(Action<bool,string> check)
    {
        MetadataBox.IsChecked=false;Metadata_Click(MetadataBox,new RoutedEventArgs());
        var now=DateTimeOffset.UtcNow;const string ip="202.97.63.30",second="218.30.53.210";
        const string legacyIp="202.97.63.31";
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=AppPaths.DatabasePath}.ToString()))
        {
            db.Open();using var cmd=db.CreateCommand();cmd.CommandText="INSERT OR REPLACE INTO node_metadata VALUES($ip,$expiry,$json)";
            cmd.Parameters.AddWithValue("$ip",legacyIp);cmd.Parameters.AddWithValue("$expiry",now.AddDays(7).ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$json","""{"Address":"202.97.63.31","Success":true,"Asn":4134,"Isp":"CHINANET","Organization":"CHINANET","Country":"United States","Region":"California","City":"San Jose","Queried":"2026-09-09T00:00:00+00:00","Expires":"2026-09-16T00:00:00+00:00","Message":"","Source":"ipwho.is"}""");cmd.ExecuteNonQuery();
        }
        var legacyRun=new RouteRun(Guid.NewGuid().ToString("N"),_primaryTarget!.Key,_primaryTarget.Address,"legacy fixture",now,now,"fixture","partial",false,1000,32,new HopProbe[]{new(1,1,legacyIp,11013,150)});
        _history.SaveRoute(legacyRun);SetWorkspace(1);PageBox.SelectedIndex=1;await RefreshAsync();RenderRoute(legacyRun);await _annotationTask;
        var legacyLabels=RouteRows.Children.OfType<Border>().Select(b=>(StackPanel)b.Child).SelectMany(p=>p.Children.OfType<TextBlock>()).Select(t=>t.Text).ToArray();
        check(legacyLabels.Any(s=>s.Contains("电信 163")&&s.Contains("圣何塞"))&&legacyLabels.All(s=>!s.Contains("正在读取节点注释")),"NativeAOT reads genuine old-format metadata without leaving loading placeholders");
        var waiting=new TextBlock{Text="正在读取节点注释…"};var retained=new TextBlock{Text="已有节点证据"};
        var failedRun=legacyRun with{Id=Guid.NewGuid().ToString("N"),Address="invalid-fixture-address"};_displayedRoute=failedRun;
        await FillAnnotationsAsync(failedRun,new Dictionary<string,List<TextBlock>>{{legacyIp,new List<TextBlock>{waiting,retained}}},false,CancellationToken.None);_displayedRoute=legacyRun;
        check(waiting.Text.Contains("读取失败")&&retained.Text=="已有节点证据","annotation read failure clears loading placeholders and preserves displayed evidence");
        NodeMetadata Meta(string address,string provider,string region,string city)=>new(address,true,4134,"CHINANET","CHINANET","United States",region,city,now,now.AddDays(7),""){ProviderId=provider};
        _history.SaveNodeMetadata(Meta(ip,"ipwho.is","District of Columbia","Washington"));_history.SaveNodeMetadata(Meta(ip,"ipinfo-core","California","San Jose"));
        _history.SaveNodeMetadata(Meta(second,"ipwho.is","California","San Jose"));_history.SaveNodeMetadata(Meta(second,"ipinfo-core","California","Fremont"));
        var target=_primaryTarget!;var run=new RouteRun(Guid.NewGuid().ToString("N"),target.Key,target.Address,"geo UI fixture",now,now,"fixture","partial",false,1000,32,
            new HopProbe[]{new(1,1,"172.16.10.1",11013,1),new(2,1,ip,11013,149.6),new(3,1,second,11013,153.7)});
        _history.SaveRoute(run);var annotation=_history.ReinterpretWithCurrentEvidence(run) ?? throw new InvalidOperationException("模拟路由注释未保存");SetOverviewTable(run,annotation);
        check(_overviewTableModel!.Rows.Where(r=>r.Addresses.Length>0).All(r=>!r.Merged)&&_overviewTableModel.Rows.Any(r=>r.Nodes[0].Location.Contains("待核对")),"overview renders city conflicts without merging them");
        check(OverviewRouteGrid.Children.OfType<Button>().Count(b=>Grid.GetColumn(b)==2)==2,"overview location cells expose clickable evidence actions");
        SetWorkspace(1);PageBox.SelectedIndex=0;await RefreshAsync();SetOverviewTable(run,annotation);OverviewRouteSummary.Text=annotation.Summary;await CaptureForVerificationAsync("-geo-conflict");
        _geoTestAction=async dialog=>
        {
            check(dialog.Title.ToString()!.Contains(ip)&&GeoVisuals(dialog).OfType<TextBlock>().Any(t=>t.Text.Contains("Washington")),"calibration dialog shows source evidence for the chosen IP");
            var fields=GeoVisuals(dialog).OfType<TextBox>().ToArray();
            fields.First(t=>t.Header?.ToString()=="国家 / 地区").Text="美国";
            fields.First(t=>t.Header?.ToString()=="省 / 州（可选）").Text="加州";
            fields.First(t=>t.Header?.ToString()=="城市（可选）").Text="圣何塞";
            fields.First(t=>t.Header?.ToString()=="校准依据（必填）").Text="UI 自检模拟依据";
            await CaptureForVerificationAsync("-geo-calibration",dialog);InvokeGeoButton(dialog,"保存校准");
        };
        await ShowNodeCalibrationAsync(ip);
        check(_history.LoadCalibration(ip)?.City=="圣何塞"&&_history.LoadRouteAnnotation(run.Id,true)!.Id==annotation.Id,"native calibration saves without replacing earliest route evidence");
        _geoTestAction=dialog=>{InvokeGeoButton(dialog,"撤销校准");return Task.CompletedTask;};await ShowNodeCalibrationAsync(ip);
        check(_history.LoadCalibration(ip) is null&&NodeClassifier.Locate(_history.LoadNodeMetadata(ip),now).Conflict,"native undo returns the node to its unresolved source evidence");
        GeoTokenStore.Save("native-test-token");check(GeoTokenStore.Read()=="native-test-token","NativeAOT DPAPI token round trip succeeds");
        check(!File.ReadAllText(AppPaths.SettingsPath).Contains("native-test-token")&&!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(AppPaths.DataDirectory,"ipinfo-token.dpapi"))).Contains("native-test-token"),"token is encrypted and absent from settings JSON");
        GeoTokenStore.Save("");check(GeoTokenStore.Read()=="","removing a token clears the encrypted file");
        _geoTestAction=async dialog=>
        {
            GeoVisuals(dialog).OfType<ComboBox>().Single().SelectedIndex=1;
            GeoVisuals(dialog).OfType<PasswordBox>().Single(p=>p.Name=="IpinfoTokenInput").Password="provider-ui-token";
            GeoVisuals(dialog).OfType<CheckBox>().First(c=>c.Content?.ToString()?.Contains("双源")==true).IsChecked=true;
            await CaptureForVerificationAsync("-geo-settings",dialog);InvokeGeoButton(dialog,"保存");
        };
        await ShowGeoSettingsAsync();
        check(Settings.Load().GeoPrimary=="ipinfo-core"&&Settings.Load().GeoCrossCheck&&_metadataClient!.Options.Primary=="ipinfo-core","data source dialog persists primary provider and cross-check preference");
        check(_annotationService!.Mode==0,"saving a data source does not enable disabled online annotations");
        _geoTestAction=async dialog=>
        {
            GeoVisuals(dialog).OfType<ComboBox>().Single().SelectedIndex=2;
            GeoVisuals(dialog).OfType<PasswordBox>().Single(p=>p.Name=="NextTraceTokenInput").Password="nexttrace-ui-fixture";
            GeoVisuals(dialog).OfType<NumberBox>().Single().Value=24;
            check(GeoVisuals(dialog).OfType<Button>().Any(b=>b.Content?.ToString()?.Contains("领取 / 刷新") ==true)&&NodeMetadataClient.NextTraceTokenPage=="https://api.nxtrace.org/v4/api-tokens","NextTrace settings expose the official token page action");
            await CaptureForVerificationAsync("-nexttrace-settings",dialog);InvokeGeoButton(dialog,"保存");
        };
        await ShowGeoSettingsAsync();
        check(Settings.Load().GeoPrimary=="nexttrace"&&Settings.Load().GeoRefreshHours==24&&_metadataClient!.Options.NextTraceToken=="nexttrace-ui-fixture","NextTrace provider and daily cache settings persist through the native dialog");
        check(GeoTokenStore.Read("nexttrace")=="nexttrace-ui-fixture"&&!File.ReadAllText(AppPaths.SettingsPath).Contains("nexttrace-ui-fixture")&&!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(AppPaths.DataDirectory,"nexttrace-token.dpapi"))).Contains("nexttrace-ui-fixture"),"NextTrace token is DPAPI encrypted and absent from exported settings");
        check(!GeoVisuals(RootGrid).OfType<Button>().Any(b=>b.Content?.ToString() is "导入 NextTrace" or "导入记录" or "复制命令"),"legacy NextTrace import and clipboard buttons are removed");
        _geoTestAction=dialog=>
        {
            GeoVisuals(dialog).OfType<ComboBox>().Single().SelectedIndex=0;
            foreach(var box in GeoVisuals(dialog).OfType<CheckBox>())box.IsChecked=box.Content?.ToString()?.Contains("移除")==true;
            InvokeGeoButton(dialog,"保存");return Task.CompletedTask;
        };
        await ShowGeoSettingsAsync();
        check(GeoTokenStore.Read()==""&&_metadataClient!.Options.Primary=="ipwho.is"&&!_metadataClient.Options.CrossCheck,"data source dialog removes the token and returns to the base provider");
        _geoTestAction=null;
        var originalTheme=RootGrid.RequestedTheme;RootGrid.RequestedTheme=ElementTheme.Light;
        SetOverviewTable(run,_history.ReinterpretWithCurrentEvidence(run));await CaptureForVerificationAsync("-geo-light");RootGrid.RequestedTheme=originalTheme;
    }
}

