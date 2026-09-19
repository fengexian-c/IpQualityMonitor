param([string]$Dotnet='dotnet',[string]$Packages='',[ValidateSet('all','locations','table','routes','route-history','multi','geo')][string]$Selection='all')
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_HOME=Join-Path $PSScriptRoot '.dotnet-home'
$env:APPDATA=Join-Path $PSScriptRoot '.appdata'
New-Item -ItemType Directory -Path $env:APPDATA -Force | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$taskOptions=@('-p:NuGetAudit=false')
if($Packages){$env:NUGET_PACKAGES=$Packages;$taskOptions+=('-p:RestoreSources='+$Packages)}
if($Selection -ne 'all'){$taskOptions+=@('--',('--'+$Selection))}
& $Dotnet run --project (Join-Path $PSScriptRoot 'tests\TcpLatencyMonitor.Tests\TcpLatencyMonitor.Tests.csproj') -c Release @taskOptions
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
