param([string]$Dotnet='dotnet',[string]$Packages='',[string]$OutputRoot='')
$ErrorActionPreference='Stop'
if(!$OutputRoot){$OutputRoot=Join-Path $PSScriptRoot 'artifacts'}
$taskRoot=[IO.Path]::GetFullPath($OutputRoot)
$taskStage=Join-Path $taskRoot ('.stage-'+[Guid]::NewGuid().ToString('N'))
$taskPackage=Join-Path $taskRoot 'IpQualityMonitor-v2.7.1-win-x64'
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
& (Join-Path $PSScriptRoot 'build.ps1') -Dotnet $Dotnet -Packages $Packages -Publish -Output $taskStage
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
foreach($taskFile in @('IpQualityMonitor.exe','IpQualityMonitor.pri','App.xbf','MainWindow.xbf','e_sqlite3.dll','NextTraceGeoHelper.exe')){
 if(!(Test-Path -LiteralPath (Join-Path $taskStage $taskFile))){throw "发布文件缺失：$taskFile"}
}
# Stage is newly created by this invocation. Never package or delete live user data.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $taskStage '使用说明.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE.reference') -Destination $taskStage
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs') -Destination (Join-Path $taskStage 'docs') -Recurse
foreach($taskSymbol in Get-ChildItem -LiteralPath $taskStage -Filter '*.pdb' -Recurse -File){Remove-Item -LiteralPath $taskSymbol.FullName}
$taskArchive=Join-Path $taskRoot ('IpQualityMonitor-v2.7.1-win-x64-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.zip')
Compress-Archive -Path (Join-Path $taskStage '*') -DestinationPath $taskArchive -CompressionLevel Optimal
# Use a fresh directory if the default runnable copy already exists.
if(Test-Path -LiteralPath $taskPackage){$taskPackage+='-'+(Get-Date -Format 'yyyyMMdd-HHmmss')}
if(![IO.Path]::GetFullPath($taskStage).StartsWith($taskRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
   ![IO.Path]::GetFullPath($taskPackage).StartsWith($taskRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '输出路径越界'}
Move-Item -LiteralPath $taskStage -Destination $taskPackage
Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256
Write-Host "可运行目录：$taskPackage"

