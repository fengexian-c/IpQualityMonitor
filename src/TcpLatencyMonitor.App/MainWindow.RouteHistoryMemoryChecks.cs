using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using TcpLatencyMonitor.App.Services;
using TcpLatencyMonitor.Core;

namespace TcpLatencyMonitor.App;

public sealed partial class MainWindow
{
    private async Task VerifyRouteHistoryMemoryAsync()
    {
        if(_settings.Profiles.Count!=0||_settings.EnableNodeMetadata)throw new InvalidOperationException("Route memory test requires empty isolated offline settings.");
        _timer.Stop();_annotationService!.Mode=0;
        var profiles=Enumerable.Range(0,3).Select(i=>new TargetProfile{Id="route-memory-"+i,Name="路由压力验证 "+i,Address="127.0.0."+(70+i),Mode="Both",Port=443}).ToArray();
        _settings.Profiles.AddRange(profiles);foreach(var profile in profiles)_compared.Add(profile.Id);
        SelectProfile(profiles[0]);PageBox.SelectedIndex=1;await RefreshAsync();
        var watch=Stopwatch.StartNew();int iteration=0,retentionCycles=0;
        int seconds=int.TryParse(Environment.GetEnvironmentVariable("IPQUALITY_ROUTE_MEMORY_SECONDS"),out int configured)?Math.Clamp(configured,30,1800):300;
        string csv=Path.Combine(AppPaths.DataDirectory,"route-memory.csv");
        File.WriteAllText(csv,"stage,seconds,iterations,private_mb,working_mb,managed_mb,allocated_mb,gen2,handles,matrix_rows,axis_marks,retained_routes,raw_parsed,full_builds,incremental_appends\n");
        void Measure(string stage)
        {
            using var process=Process.GetCurrentProcess();
            using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=AppPaths.DatabasePath}.ToString());db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM route_run";
            File.AppendAllText(csv,FormattableString.Invariant($"{stage},{watch.Elapsed.TotalSeconds:F1},{iteration},{process.PrivateMemorySize64/1048576d:F2},{process.WorkingSet64/1048576d:F2},{GC.GetTotalMemory(false)/1048576d:F2},{GC.GetTotalAllocatedBytes()/1048576d:F2},{GC.CollectionCount(2)},{process.HandleCount},{_routeMatrixRows.Count},{_routeTimeMarks.Count},{cmd.ExecuteScalar()},{_routeAnalysis!.RawJsonParsed},{_routeAnalysis.Rebuilds},{_routeAnalysis.IncrementalAppends}\n"));
            if(process.PrivateMemorySize64>1024L*1024*1024)throw new InvalidOperationException("Route memory test exceeded 1 GiB safety ceiling.");
        }
        Measure("start");
        while(watch.Elapsed.TotalSeconds<seconds)
        {
            var profile=profiles[iteration%profiles.Length];var target=profile.Primary;var now=DateTimeOffset.UtcNow;bool silent=iteration%7==0;
            string[] ips=["10.0.0.1","192.0.2.2","192.0.2.3","198.51.100."+(10+iteration/3%3),"192.0.2.5",target.Address];
            var run=new RouteRun("memory-route-"+iteration,target.Key,target.Address,"memory-fixture",now.AddSeconds(-10),now.AddSeconds(-2),"isolated stress","synthetic",true,1500,32,
                ips.SelectMany((ip,i)=>Enumerable.Range(1,3).Select(q=>new HopProbe(i+1,q,silent&&i==3?null:ip,silent&&i==3?11010:i==5?0:11013,silent&&i==3?null:10+i){SentAt=now.AddSeconds(-9+i),IsSupplemental=q==3})).ToArray()){ProbeExecutionId="memory-route-"+iteration,ProbeOptions=new()};
            await _history.RecordAsync(()=>{_history.SaveRoute(run);_history.Add(target,new(now,ProbeStatus.Success,10+iteration%20,10,"synthetic"));_history.Add(profile.Secondary!,new(now,ProbeStatus.Success,200+iteration%20,200,"synthetic"));});
            if(iteration%25==0)
            {
                SelectProfile(profiles[iteration/25%3]);PeriodBox.SelectedIndex=iteration/25%2;ViewProtocolBox.SelectedIndex=iteration/50%2;
                _routeHistoryReading=false;
            }
            int phase=iteration/35%4;SetWorkspace(phase<2?1:phase==2?0:2);PageBox.SelectedIndex=phase<2?1:0;
            _recordsDirty=true;_multiLoaded=DateTimeOffset.MinValue;await RefreshAsync();await _routeMatrixTask;
            if(phase==1&&_routeHistoryWindow.LastOrDefault() is {} member&&!_routeHistoryReading)await SelectHistoryObservationAsync(member,false);
            if(phase<2&&iteration%12==4&&_routeHistoryWindow.LastOrDefault() is {} raw){await LoadRawRouteAsync(raw.Observation.Id);await _annotationTask;}
            if(iteration%12==6&&RouteRawPanel.Visibility==Visibility.Visible)RouteRawToggle_Click(RouteRawPanel,new RoutedEventArgs());
            if(iteration%60==40){HideToTray();await Task.Delay(100);RestoreFromTray();await RefreshAsync();}
            if(iteration>0&&iteration%40==0)
            {
                using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=AppPaths.DatabasePath}.ToString());db.Open();using var cmd=db.CreateCommand();
                cmd.CommandText="DELETE FROM route_run WHERE id IN (SELECT id FROM route_run WHERE target=$t AND id LIKE 'memory-route-%' ORDER BY time_ms DESC LIMIT -1 OFFSET 60)";
                var p=cmd.Parameters.Add("$t",SqliteType.Text);foreach(var entry in profiles){p.Value=entry.Primary.Key;cmd.ExecuteNonQuery();}retentionCycles++;
                _history.AddEvent(new("memory-boundary-"+iteration,target.Key,now,"Stopped","simulated retention/boundary"));
            }
            iteration++;if(iteration%25==0)Measure("refresh");await Task.Delay(200);
        }
        Measure("end");
        if(_routeMatrixRows.Count>64||_routeTimeMarks.Count>960)throw new InvalidOperationException("Route memory test found an unbounded visual pool.");
        File.WriteAllText(Path.Combine(AppPaths.DataDirectory,"route-memory-done.txt"),FormattableString.Invariant($"PASS {watch.Elapsed.TotalSeconds:F1} seconds; {iteration} synthetic routes; 3 targets; {retentionCycles} retention cycles; following/frozen/overview/comparison, range/protocol switches and tray; no forced GC."));
        SetWorkspace(1);PageBox.SelectedIndex=1;await RefreshAsync();await CaptureForVerificationAsync("-route-memory");await ExitAsync();
    }
}
