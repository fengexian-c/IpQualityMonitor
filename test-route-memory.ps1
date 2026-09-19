param(
    [string]$PublishedDirectory=(Join-Path $PSScriptRoot 'artifacts\IpQualityMonitor-win-x64'),
    [ValidateRange(30,1800)][int]$Seconds=300
)
$ErrorActionPreference='Stop'
$taskExecutable=Join-Path ([IO.Path]::GetFullPath($PublishedDirectory)) 'IpQualityMonitor.exe'
if(!(Test-Path -LiteralPath $taskExecutable -PathType Leaf)){throw '请先自包含发布，或用 -PublishedDirectory 指定运行包目录。'}
$taskData=Join-Path $PSScriptRoot ('artifacts\route-memory-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6))
New-Item -ItemType Directory -Path $taskData | Out-Null
@{Version=4;Profiles=@();ArchivedProfiles=@();EnableNodeMetadata=$false;BackgroundNodeMetadata=$false;EnableRoutes=$false;ResumeOnLaunch=$false} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskData 'settings.json') -Encoding utf8
$taskVariables=@{TCP_MONITOR_DATA_DIR=$taskData;IPQUALITY_ROUTE_MEMORY_AUDIT='1';IPQUALITY_ROUTE_MEMORY_SECONDS=$Seconds.ToString();TCP_MONITOR_CAPTURE_PATH=(Join-Path $taskData 'ui.png')}
$taskPrevious=@{}
$taskProcess=$null
try {
    foreach($entry in $taskVariables.GetEnumerator()){
        $taskPrevious[$entry.Key]=[Environment]::GetEnvironmentVariable($entry.Key,'Process')
        [Environment]::SetEnvironmentVariable($entry.Key,$entry.Value,'Process')
    }
    $taskProcess=Start-Process -FilePath $taskExecutable -WorkingDirectory (Split-Path $taskExecutable) -WindowStyle Hidden -PassThru
    Write-Output "独立模拟测试目录：$taskData"
    $taskClock=[Diagnostics.Stopwatch]::StartNew();$taskLastReport=0
    $taskDone=Join-Path $taskData 'route-memory-done.txt'
    while(!(Test-Path -LiteralPath $taskDone)){
        if($taskProcess.HasExited){throw "测试进程提前退出，检查 $taskData 中的日志。"}
        if($taskClock.Elapsed.TotalSeconds -gt $Seconds+120){throw '测试超过预期时间，检查独立目录中的日志。'}
        if($taskClock.Elapsed.TotalSeconds-$taskLastReport -ge 30){
            $taskCsv=Join-Path $taskData 'route-memory.csv'
            if(Test-Path -LiteralPath $taskCsv){Get-Content -LiteralPath $taskCsv -Tail 1 | Write-Output}
            $taskLastReport=$taskClock.Elapsed.TotalSeconds
        }
        Start-Sleep -Seconds 2
    }
    Get-Content -LiteralPath $taskDone | Write-Output
    if(!$taskProcess.WaitForExit(30000)){throw '测试已完成，但程序未能按时退出。'}
    Write-Output "CSV 内存趋势及界面截图已保留：$taskData"
}
finally {
    if($taskProcess -and !$taskProcess.HasExited){Stop-Process -Id $taskProcess.Id -Force}
    foreach($entry in $taskPrevious.GetEnumerator()){[Environment]::SetEnvironmentVariable($entry.Key,$entry.Value,'Process')}
}
