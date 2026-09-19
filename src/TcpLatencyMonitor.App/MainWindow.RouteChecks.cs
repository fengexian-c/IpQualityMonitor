using Microsoft.UI.Xaml.Controls;
using TcpLatencyMonitor.Core;

namespace TcpLatencyMonitor.App;

public sealed partial class MainWindow
{
    private async Task VerifyRouteRepliesAsync(Action<bool,string> check)
    {
        var now=DateTimeOffset.UtcNow;var target=_primaryTarget!;
        var samples=new List<HopProbe>{new(7,1,null,11010,null),new(7,2,"202.97.43.82",11013,5),new(7,3,null,11010,null)};
        samples.AddRange(Enumerable.Range(1,10).Select(q=>new HopProbe(8,q,null,11010,null){IsSupplemental=q>3,SentAt=now.AddSeconds(q)}));
        samples.AddRange(Enumerable.Range(1,3).Select(q=>new HopProbe(9,q,null,11010,null)));
        samples.Add(new(9,4,"59.43.250.50",11013,12){IsSupplemental=true,SentAt=now.AddSeconds(4)});
        var run=new RouteRun(Guid.NewGuid().ToString("N"),target.Key,target.Address,"fixture",now,now.AddSeconds(12),
            "模拟初测 / 补测布局","界面夹具，非真实网络测量",false,1500,32,samples)
            {ProbeOptions=new(),InitialReached=false,SupplementOutcome="补测 8 次 · 补全 1 跳"};
        _history.SaveRoute(run);await _annotationService!.Request(run,false);
        PageBox.SelectedIndex=1;await RefreshAsync();RenderRoute(run);await _annotationTask;
        var cards=RouteRows.Children.OfType<Border>().Select(b=>(StackPanel)b.Child).ToArray();
        string Header(int i)=>((TextBlock)cards[i].Children[0]).Text;
        check(Header(0)=="第 7 跳 · 回应 1/3 · 超时 2 次"&&cards[0].Children.OfType<Microsoft.UI.Xaml.FrameworkElement>().Count(c=>c.Visibility==Microsoft.UI.Xaml.Visibility.Visible)==3,
            "native details show one response and its annotation with timeout count only in the heading");
        check(Header(1).Contains("回应 0/10 · 超时 10 次（初测 0/3，补测 0/7）")&&cards[1].Children.OfType<Microsoft.UI.Xaml.FrameworkElement>().Count(c=>c.Visibility==Microsoft.UI.Xaml.Visibility.Visible)==1,
            "native all-silent card contains just its heading, without any placeholder rows");
        check(Header(2).Contains("初测 0/3，补测 1/1")&&((TextBlock)cards[2].Children[1]).Text.Contains("补测 12 ms"),
            "native retry card distinguishes supplemental response provenance and latency");
        check(!cards.SelectMany(c=>c.Children.OfType<TextBlock>()).Any(t=>t.Text.Contains("未回应")),
            "native route details remove duplicated no-response labels regardless of probe ordering");
        await CaptureForVerificationAsync("-route-replies");
        var overview=run with{Probes=samples.Select(p=>p with{Ttl=p.Ttl-6}).ToArray()};
        SetOverviewTable(overview,RouteClassifier.Classify(overview,new Dictionary<string,NodeMetadata>(),now,"fixture"));
        check(OverviewRouteGrid.Children.OfType<TextBlock>().Any(t=>t.Text.Contains("补测"))&&_overviewTableModel!.HiddenNoReply==1,
            "native overview marks recovered nodes as supplemental and still filters silent TTLs");
        var persisted=_history.LoadRoutes(target).FirstOrDefault(r=>r.Context!="fixture"&&r.ProbeOptions is not null);
        check(persisted?.ProbeOptions is {MaxAttemptsPerHop:10,InitialSpacingMs:400,SupplementSpacingMs:1200}&&persisted.Probes.All(p=>p.SentAt.HasValue),
            "NativeAOT persistence retains the new policy and actual send timestamps from loopback measurement");
        PageBox.SelectedIndex=0;_overviewRouteDirty=true;await RefreshAsync();
    }
}
