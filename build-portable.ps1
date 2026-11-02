param([string]$Destination)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$workspace = Split-Path -Parent $repo
$sdk = Join-Path $workspace 'output/dotnet-sdk-portable'
if (-not $Destination) { $Destination = Join-Path $workspace 'output/SourceGit-Portable/SourceGit' }
$env:DOTNET_ROOT = $sdk
$env:DOTNET_CLI_HOME = Join-Path $workspace 'output/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $workspace 'output/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
& (Join-Path $sdk 'dotnet.exe') publish (Join-Path $repo 'src/SourceGit.csproj') -c Release -r win-x64 --self-contained true -p:DisableAOT=true -o $Destination
if ($LASTEXITCODE -ne 0) { throw 'SourceGit publish failed' }
New-Item -ItemType Directory -Force (Join-Path $Destination 'data') | Out-Null
$gitSource = Join-Path $workspace 'output/BranchDesk/release/git'
New-Item -ItemType Directory -Force (Join-Path $Destination 'git') | Out-Null
Copy-Item -Path (Join-Path $gitSource '*') -Destination (Join-Path $Destination 'git') -Recurse -Force
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
$manifest = @{built=(Get-Date -Format o);upstream='c0f40a2e78d81a7474628847727644fc0c56f558';local=(git -C $repo rev-parse HEAD);runtime='win-x64 self-contained .NET 10';files=@(Get-ChildItem -LiteralPath $Destination -File | ForEach-Object {@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $Destination 'build-manifest.json')
Write-Output "Portable build ready: $Destination"
