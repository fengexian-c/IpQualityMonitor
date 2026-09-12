param([string]$Go='go')
$ErrorActionPreference='Stop'
$env:CGO_ENABLED='0'
$taskOutput=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\third_party\nexttrace\NextTraceGeoHelper.exe'))
Push-Location $PSScriptRoot
try {
 & $Go test -timeout 30s .
 if($LASTEXITCODE -ne 0){throw 'Helper tests failed'}
 & $Go build -buildvcs=false -trimpath -ldflags '-s -w' -o $taskOutput .
 if($LASTEXITCODE -ne 0){throw 'Helper build failed'}
 Get-FileHash -LiteralPath $taskOutput -Algorithm SHA256
} finally {Pop-Location}
