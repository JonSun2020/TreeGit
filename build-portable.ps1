param([string]$Destination, [switch]$IntegrationTests)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$workspace = Split-Path -Parent $repo
$sdk = Join-Path $workspace 'output/dotnet-sdk-portable'
if (-not $Destination) { $Destination = Join-Path $workspace 'output/SourceGit-Portable/SourceGit' }
$Destination = [System.IO.Path]::GetFullPath($Destination)
$env:DOTNET_ROOT = $sdk
$env:DOTNET_CLI_HOME = Join-Path $workspace 'output/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $workspace 'output/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$testProperty = '-p:EnableIntegrationTests=' + $IntegrationTests.IsPresent.ToString().ToLowerInvariant()
& (Join-Path $sdk 'dotnet.exe') publish (Join-Path $repo 'src/SourceGit.csproj') -c Release -r win-x64 --self-contained true -p:DisableAOT=true $testProperty -o $Destination
if ($LASTEXITCODE -ne 0) { throw 'SourceGit publish failed' }
New-Item -ItemType Directory -Force (Join-Path $Destination 'data') | Out-Null
$gitSource = Join-Path $workspace 'output/portablegit-full'
if (-not (Test-Path -LiteralPath (Join-Path $gitSource 'git-bash.exe'))) { throw 'Prepared PortableGit distribution is missing' }
New-Item -ItemType Directory -Force (Join-Path $Destination 'git') | Out-Null
& robocopy $gitSource (Join-Path $Destination 'git') /E /MT:16 /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
if ($LASTEXITCODE -ge 8) { throw 'Portable Git copy failed' }
# Explicit Git path interpolation avoids the bundled libcurl default CA path's
# Unicode conversion failure. System config stays below user/repository CA overrides.
[System.IO.File]::AppendAllText((Join-Path $Destination 'git/etc/gitconfig'), "`n[http]`n`tsslCAInfo = %(prefix)/etc/ssl/certs/ca-bundle.crt`n", [System.Text.UTF8Encoding]::new($false))
Get-ChildItem (Join-Path $workspace 'output/git-lfs') -Filter git-lfs.exe -Recurse | Select-Object -First 1 | Copy-Item -Destination (Join-Path $Destination 'git/ucrt64/libexec/git-core/git-lfs.exe')
Get-ChildItem (Join-Path $workspace 'output/git-flow') -Filter git-flow.exe -Recurse | Select-Object -First 1 | Copy-Item -Destination (Join-Path $Destination 'git/ucrt64/libexec/git-core/git-flow.exe')
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'THIRD-PARTY-LICENSES.md'),(Join-Path $repo 'CUSTOMIZATION.md') -Destination $Destination -Force
Copy-Item -LiteralPath (Join-Path $workspace 'output/SourceGit-Portable/licenses') -Destination $Destination -Recurse -Force
$mcp = @{mcpServers=@{sourcegit=@{command=(Join-Path $Destination 'SourceGit.exe');args=@('--mcp')}}}
$mcp | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $Destination 'mcp-readonly.json')
$mcp.mcpServers.sourcegit.args = @('--mcp','--allow-write')
$mcp | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $Destination 'mcp-readwrite.json')
$manifest = @{built=(Get-Date -Format o);upstream='c0f40a2e78d81a7474628847727644fc0c56f558';local=(git -C $repo rev-parse HEAD);runtime='win-x64 self-contained .NET 10';components=@{git='2.56.0.windows.4';openssh='10.6p1';openssl='3.5.9';lfs='3.8.1';gitflow='2.1.0'};files=@(Get-ChildItem -LiteralPath $Destination -File | Where-Object {$_.Name -ne 'build-manifest.json'} | ForEach-Object {@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $Destination 'build-manifest.json')
Write-Output "Portable build ready: $Destination"
