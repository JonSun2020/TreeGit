param([string]$Destination, [switch]$IntegrationTests)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
if(-not $Destination){$Destination=Join-Path $workspace 'output/TreeGit/TreeGit'}
$Destination=[IO.Path]::GetFullPath($Destination)
$sdk=Join-Path $workspace 'output/dotnet-sdk-portable'
if (Test-Path (Join-Path $sdk 'dotnet.exe')) {
    $env:DOTNET_ROOT=$sdk
    $env:DOTNET_CLI_HOME=Join-Path $workspace 'output/dotnet-home'
    $env:NUGET_PACKAGES=Join-Path $workspace 'output/nuget'
    $dotnet=Join-Path $sdk 'dotnet.exe'
} else {
    $dotnet=(Get-Command dotnet -ErrorAction Stop).Source
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$treeGitSourceCommit=(& git -C $PSScriptRoot rev-parse HEAD).Trim()
$testProperty='-p:EnableIntegrationTests='+$IntegrationTests.IsPresent.ToString().ToLowerInvariant()
& $dotnet publish (Join-Path $PSScriptRoot 'src/SourceGit.csproj') -c Release -r win-x64 -p:PublishAot=false -p:PublishTrimmed=true -p:TrimMode=link -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false $testProperty -o $Destination
if($LASTEXITCODE -ne 0){throw 'TreeGit publish failed'}
Move-Item -LiteralPath (Join-Path $Destination 'SourceGit.exe') -Destination (Join-Path $Destination 'TreeGit.exe') -Force
New-Item -ItemType Directory -Force (Join-Path $Destination 'data') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE'),(Join-Path $PSScriptRoot 'THIRD-PARTY-LICENSES.md') -Destination $Destination -Force
$config=@{mcpServers=@{treegit=@{command=(Join-Path $Destination 'TreeGit.exe');args=@('--mcp','--read-only')}}}
$config | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $Destination 'mcp-readonly.json')
$config.mcpServers.treegit.args=@('--mcp')
$config | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $Destination 'mcp-readwrite.json')
@{product='TreeGit';sourceCommit=$treeGitSourceCommit;upstreamCommit='c0f40a2e78d81a7474628847727644fc0c56f558';integrationTests=$IntegrationTests.IsPresent;selfContained=$true;trimmed=$true;singleFileCompression=$true} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Destination 'build-info.json')
Write-Output ('TreeGit portable program: '+$Destination)
