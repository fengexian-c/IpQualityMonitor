param([string]$Dotnet="dotnet",[string]$Packages="",[switch]$Publish,[string]$Output="",[switch]$Lean)
$ErrorActionPreference="Stop"
$env:DOTNET_CLI_HOME=Join-Path $PSScriptRoot '.dotnet-home'
$env:APPDATA=Join-Path $PSScriptRoot '.appdata'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
if($Packages){$env:NUGET_PACKAGES=$Packages}
New-Item -ItemType Directory -Path $env:APPDATA -Force | Out-Null
$taskProject=Join-Path $PSScriptRoot 'src\TcpLatencyMonitor.App\TcpLatencyMonitor.App.csproj'
$taskOptions=@('-c','Release','-p:Platform=x64','-p:RuntimeIdentifiers=win-x64','-p:NuGetAudit=false')
if($Packages){$taskOptions+=('-p:RestoreSources='+$Packages)}
if($Publish){
 if(!$Output){$Output=Join-Path $PSScriptRoot 'artifacts\IpQualityMonitor-win-x64'}
 $taskSelfContained=if($Lean){'false'}else{'true'}
 & $Dotnet publish $taskProject @taskOptions -r win-x64 --self-contained true -p:PublishAot=true -p:PublishTrimmed=true -p:WindowsAppSDKSelfContained=$taskSelfContained -p:OptimizationPreference=Size -p:IlcFoldIdenticalMethodBodies=true -p:CopyOutputSymbolsToPublishDirectory=false -o $Output
}else{& $Dotnet build $taskProject @taskOptions}
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
