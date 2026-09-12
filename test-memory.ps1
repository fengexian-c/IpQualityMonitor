param(
 [Parameter(Mandatory=$true)][string]$Exe,
 [ValidateSet('full','stress','slow','overview','comparison')][string]$Mode='full',
 [ValidateRange(0,10000)][int]$Iterations=0
)
$ErrorActionPreference='Stop'
$taskExe=[IO.Path]::GetFullPath($Exe)
if(!(Test-Path -LiteralPath $taskExe -PathType Leaf)){throw '请指定已编译的 IpQualityMonitor.exe。'}
$taskDirectory=Join-Path $PSScriptRoot ('artifacts\memory-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $taskDirectory 'settings.json'),'{"Version":4,"Profiles":[],"EnableNodeMetadata":false,"BackgroundNodeMetadata":false}',[Text.UTF8Encoding]::new($false))
$taskNames=@('TCP_MONITOR_DATA_DIR','IPQUALITY_MEMORY_AUDIT','IPQUALITY_MEMORY_CASE','IPQUALITY_MEMORY_ITERATIONS','TCP_MONITOR_CAPTURE_PATH','TCP_MONITOR_SMOKE_TEST','TCP_MONITOR_V4_SELF_TEST')
$taskPrevious=@{}
foreach($taskName in $taskNames){$taskPrevious[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process')}
$taskProcess=$null
try{
 $env:TCP_MONITOR_DATA_DIR=$taskDirectory
 $env:IPQUALITY_MEMORY_AUDIT='1'
 $env:IPQUALITY_MEMORY_CASE=$Mode
 $env:IPQUALITY_MEMORY_ITERATIONS=if($Iterations -gt 0){$Iterations.ToString()}else{''}
 $env:TCP_MONITOR_CAPTURE_PATH=Join-Path $taskDirectory 'render.png'
 $env:TCP_MONITOR_SMOKE_TEST=''
 $env:TCP_MONITOR_V4_SELF_TEST=''
 $taskProcess=Start-Process -FilePath $taskExe -WindowStyle Hidden -PassThru
 if(!$taskProcess.WaitForExit(900000)){throw '测试超过十五分钟截止时间。'}
 if($taskProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $taskDirectory 'memory-done.txt'))){throw "测试未正常结束，退出码 $($taskProcess.ExitCode)，请查看 $taskDirectory 中的日志。"}
 Get-Content -LiteralPath (Join-Path $taskDirectory 'memory.csv')
 Write-Host "测试结果：$taskDirectory"
}finally{
 if($taskProcess -and !$taskProcess.HasExited){$taskProcess.Kill()}
 foreach($taskName in $taskNames){[Environment]::SetEnvironmentVariable($taskName,$taskPrevious[$taskName],'Process')}
}
