param([Parameter(Mandatory=$true)][string]$Source, [string]$Destination=$PSScriptRoot)
$ErrorActionPreference='Stop'
$Source=[IO.Path]::GetFullPath($Source)
$Destination=[IO.Path]::GetFullPath($Destination)
$info=Get-Content -LiteralPath (Join-Path $Source 'build-info.json') -Raw | ConvertFrom-Json
if($info.product -ne 'TreeGit' -or $info.version -notmatch '^\d+\.\d+\.\d+$' -or $info.integrationTests){throw 'A normal TreeGit release build is required'}
if(-not (Test-Path -LiteralPath (Join-Path $Source 'TreeGit.exe'))){throw 'TreeGit.exe is missing'}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$name='TreeGit-'+$info.version+'-Windows-x64.zip'
$archive=Join-Path $Destination $name
if(Test-Path -LiteralPath $archive){throw 'This versioned archive already exists; choose a new version or output directory'}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try {
    # Only program files from the build output. Never package preferences, cache or credentials.
    $programFiles=@('TreeGit.exe','LICENSE','THIRD-PARTY-LICENSES.md','使用说明.md','MCP-使用说明.md','mcp-readonly.json','mcp-readwrite.json','build-info.json')
    foreach($file in Get-ChildItem -LiteralPath $Source -File){
        if($file.Name -notin $programFiles -and $file.Extension -ne '.dll'){continue}
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,('TreeGit/'+$file.Name),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $zip.CreateEntry('TreeGit/data/') | Out-Null
} finally {$zip.Dispose()}
$hashAlgorithm=[Security.Cryptography.SHA256]::Create()
$archiveStream=[IO.File]::OpenRead($archive)
try {$hash=([BitConverter]::ToString($hashAlgorithm.ComputeHash($archiveStream))).Replace('-','').ToLowerInvariant()}
finally {$archiveStream.Dispose();$hashAlgorithm.Dispose()}
[IO.File]::WriteAllText((Join-Path $Destination 'SHA256SUMS.txt'),($hash+'  '+$name+"`n"),[Text.UTF8Encoding]::new($false))
Write-Output $archive
