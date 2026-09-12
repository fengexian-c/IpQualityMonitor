using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using TcpLatencyMonitor.App.Controls;
using TcpLatencyMonitor.App.Services;
using TcpLatencyMonitor.Core;

namespace TcpLatencyMonitor.App;
public sealed partial class MainWindow
{
    private async Task VerifyRenderingAsync(Action<bool,string> check)
    {
        _timer.Stop();
        try
        {
            SetWorkspace(0);_multiLoaded=DateTimeOffset.MinValue;await RefreshAsync();await Task.Delay(150);
            var rows=_overviewRows.ToDictionary(p=>p.Key,p=>p.Value);int hiddenDraws=CompareChart.RenderCount;
            for(int i=0;i<6;i++){RenderAllTargets();RenderComparison();await Task.Delay(20);}
            check(rows.Count==_overviewRows.Count&&rows.All(p=>ReferenceEquals(p.Value,_overviewRows[p.Key])),"overview refresh reuses every row and chart instance");
            var removedProfile=_settings.Profiles[^1];_settings.Profiles.RemoveAt(_settings.Profiles.Count-1);RenderAllTargets();
            check(!_overviewRows.ContainsKey(removedProfile.Id)&&!_rowActions.ContainsKey(removedProfile.Id)&&AllTargetsGrid.RowDefinitions.Count==_settings.Profiles.Count+1,"removed targets release row caches and actions");
            _settings.Profiles.Add(removedProfile);RenderAllTargets();
            check(CompareChart.RenderCount==hiddenDraws,"hidden comparison page does not paint during overview refresh");
            check(_overviewRows.Values.All(r=>r.Trend.Children.Count<20),"mini charts keep a bounded visual tree for hundreds of points");
            var tipTarget=_overviewRows.Values.First().Open;
            SetTip(tipTarget,"延迟加载提示");_toolTips.TryGetValue(tipTarget,out var cachedTip);
            cachedTip!.Tip.IsOpen=false;cachedTip.Content.Text="";SetTip(tipTarget,"当前最新提示");
            bool deferred=cachedTip.Content.Text.Length==0;
            cachedTip.Tip.IsOpen=true;await Task.Delay(100);
            check(deferred&&cachedTip.Content.Text=="当前最新提示","closed tooltip defers visual updates and opening reads latest text");
            cachedTip.Tip.IsOpen=false;
            _compared.Clear();foreach(var p in _settings.Profiles.Take(4))_compared.Add(p.Id);
            SetWorkspace(2);await RefreshAsync();await Task.Delay(100);
            check(CompareChart.Series.Count>0&&_comparisonRows.Count==_compared.Count,"switching pages renders cached data without waiting fifteen seconds");
            var panels=_comparisonRows.ToDictionary(p=>p.Key,p=>p.Value);var tiles=panels.First().Value.Matrix.Cells.ToDictionary(p=>p.Key,p=>p.Value);
            RenderComparison();
            check(panels.All(p=>ReferenceEquals(p.Value,_comparisonRows[p.Key]))&&tiles.All(p=>ReferenceEquals(p.Value,panels.First().Value.Matrix.Cells[p.Key])),"comparison refresh reuses panels and hour tiles");
            var timeline=CompareChart.Series[0].Timeline;var grid=new Grid();DateTimeOffset clicked=default;
            var matrix=new HourMatrix(grid,(from,until,description)=>clicked=from);matrix.Update(timeline,false,0,300);
            var first=matrix.Cells.First();var before=first.Value.From;var tooltipContent=first.Value.Tip.Content;
            var shifted=timeline with{From=timeline.From.AddDays(1),Until=timeline.Until.AddDays(1),Hours=timeline.Hours.Select(b=>b with{Start=b.Start.AddDays(1)}).ToArray()};
            matrix.Update(shifted,false,0,300);matrix.Select(first.Key);
            check(ReferenceEquals(first.Value,matrix.Cells[first.Key])&&clicked==before.AddDays(1),"reused hour tiles select the new time window rather than stale captured times");
            first.Value.RefreshTip();
            check(ReferenceEquals(tooltipContent,first.Value.Tip.Content)&&first.Value.TipText.Text==first.Value.Description,"hour tooltip updates text without replacing its content control");
            var profile=_settings.Profiles[0];var current=profile.Copy();current.Name="更新后的目标名称";_settings.Profiles[0]=current;
            SetWorkspace(0);RenderAllTargets();var peer=new ButtonAutomationPeer(_overviewRows[profile.Id].Open);((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();await Task.Delay(200);await RefreshAsync();
            check(NameBox.Text==current.Name,"reused target button resolves the current profile after editing");_settings.Profiles[0]=profile;
            PageBox.SelectedIndex=0;SetWorkspace(1);await RefreshAsync();
            var now=DateTimeOffset.UtcNow;var buckets=new[]{new Bucket(now.AddMinutes(-4),1,1,0,0,0,0),new Bucket(now.AddMinutes(-3),0,0,1,null),new Bucket(now.AddMinutes(-2),2,1,0,20,10,240),new Bucket(now.AddMinutes(-1),1,0,0,null)};
            Chart.SetData(buckets,1,now);await Task.Delay(150);
            check(Chart.SampleMarkerCount==2&&Chart.SegmentCount==2&&Chart.FailureMarkerCount==2&&Chart.ScaleMaximum==240,"geometry preserves zero RTT, gaps, mixed failures and maximum latency");
            check(Chart.TipAt(2).Contains("240.0")&&Chart.TipAt(1).Contains("本地错误 1"),"chart tooltip reads original extrema and local-error counts");
            await CaptureForVerificationAsync("-rendering-fidelity",Chart);
            int paints=Chart.RenderCount;HideToTray();Chart.SetData(buckets,1,now);RenderAllTargets();RenderComparison();await Task.Delay(120);
            check(Chart.RenderCount==paints,"tray state suppresses chart rendering");RestoreFromTray();await RefreshAsync();await Task.Delay(100);
            _timelineKey=null;_detailLoadedKey=null;await RefreshAsync();
            await CaptureForVerificationAsync("-rendering-overview");
        }
        finally{_timer.Start();}
    }

    private async Task RunRenderingMemoryCheckAsync()
    {
        // Explicit opt-in with an empty profile set, no probing or online annotations.
        if(_settings.Profiles.Count!=0)throw new InvalidOperationException("Memory verification requires empty profiles.");
        _timer.Stop();_annotationService!.Mode=0;
        string mode=Environment.GetEnvironmentVariable("IPQUALITY_MEMORY_CASE")??"full";
        int count=mode=="slow"?40:mode=="full"?400:2000,targets=mode is "stress" or "overview" or "comparison"?10:4,days=mode is "stress" or "overview" or "comparison"?7:1;
        if(int.TryParse(Environment.GetEnvironmentVariable("IPQUALITY_MEMORY_ITERATIONS"),out int requested))count=Math.Clamp(requested,1,10000);
        var now=DateTimeOffset.UtcNow;int step=days==7?30:5,pointCount=days==7?337:289;
        Timeline MakeTimeline(int tick)
        {
            var until=now.AddSeconds(tick*15);var from=until.AddDays(-days);
            var points=Enumerable.Range(0,pointCount).Select(i=>mode is "full" or "slow"?new Bucket(from.AddMinutes(i*step),5,5,0,150+i%20,149,180):
                i%19==0?new Bucket(from.AddMinutes(i*step),0,0,1,null):new Bucket(from.AddMinutes(i*step),5,i%11==0?3:5,0,i==pointCount/2?500:150+(i+tick)%20,149,i==pointCount/2?520:180)).ToArray();
            var local=from.ToLocalTime();
            var hourStart=mode=="full"?from:new DateTimeOffset(local.Year,local.Month,local.Day,local.Hour,0,0,local.Offset).ToUniversalTime();
            var hours=Enumerable.Range(0,mode=="full"?25:(int)Math.Ceiling((until-hourStart).TotalHours)).Select(i=>new Bucket(hourStart.AddHours(i),60,60,0,160,149,180)).ToArray();
            return new(from,until,days,step,points,hours,new Summary(days*1440,days*1440,0,160,149,180));
        }
        for(int i=0;i<targets;i++)_settings.Profiles.Add(new TargetProfile{Id="memory-"+i,Name="内存验证 "+i,Address="127.0.0."+(i+1)});
        _timelineDays=days;PeriodBox.SelectedIndex=days==7?1:0;
        await RefreshAsync();
        foreach(var p in _settings.Profiles.Take(4))_compared.Add(p.Id);
        void UpdateData(int tick)
        {
            var timeline=MakeTimeline(tick);
            var latest=mode=="full"?null:new Sample(timeline.Until,ProbeStatus.Success,150+tick%20,150+tick%20,"内存验证：采样 "+tick);
            _multiOverview=new(timeline.Until,days,ProbeProtocol.Icmp,_settings.Profiles.Select(p=>new TargetOverview(p.Id,p.Primary,timeline,180,latest,null,null)).ToArray());_allTargetsDirty=_comparisonDirty=true;
        }
        UpdateData(0);SetWorkspace(0);RenderAllTargets();await Task.Delay(1500);
        string path=Path.Combine(AppPaths.DataDirectory,"memory.csv");File.WriteAllText(path,"stage,seconds,private_mb,working_mb,managed_mb,allocated_mb,gen2,handles,rows,chart_elements\n");
        var watch=Stopwatch.StartNew();
        void Measure(string stage)
        {
            using var process=Process.GetCurrentProcess();
            File.AppendAllText(path,FormattableString.Invariant($"{stage},{watch.Elapsed.TotalSeconds:F1},{process.PrivateMemorySize64/1048576d:F2},{process.WorkingSet64/1048576d:F2},{GC.GetTotalMemory(false)/1048576d:F2},{GC.GetTotalAllocatedBytes()/1048576d:F2},{GC.CollectionCount(2)},{process.HandleCount},{_overviewRows.Count},{_overviewRows.Values.Sum(r=>r.Trend.Children.Count)}\n"));
        }
        Measure("start");
        for(int i=1;i<=count;i++)
        {
            UpdateData(mode=="full"?0:i);
            if(mode is "stress" or "overview" or "comparison")
            {
                int view=mode=="overview"?0:mode=="comparison"?2:(i/100)%2==0?0:2;
                if(_workspaceView!=view)SetWorkspace(view);
            }
            RenderAllTargets();RenderComparison();UpdateLiveStates();
            await Task.Delay(mode=="slow"?15000:40);
            if(i%25==0||mode=="slow")Measure("refresh-"+i);
            using var process=Process.GetCurrentProcess();if(process.PrivateMemorySize64>1500L*1024*1024)throw new InvalidOperationException("Memory test exceeded safety ceiling.");
        }
        Measure("end");await CaptureForVerificationAsync("-memory");
        File.WriteAllText(Path.Combine(AppPaths.DataDirectory,"memory-done.txt"),"completed without forced GC");await ExitAsync();
    }
}
