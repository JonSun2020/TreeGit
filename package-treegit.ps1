param([Parameter(Mandatory=$true)][string]$Source, [string]$Destination=$PSScriptRoot)
$ErrorActionPreference='Stop'
$Source=[IO.Path]::GetFullPath($Source)
$Destination=[IO.Path]::GetFullPath($Destination)
$info=Get-Content -LiteralPath (Join-Path $Source 'build-info.json') -Raw | ConvertFrom-Json
if($info.product -ne 'TreeGit' -or $info.version -notmatch '^\d+\.\d+\.\d+$' -or $info.integrationTests){throw 'A normal TreeGit release build is required'}
if(-not (Test-Path -LiteralPath (Join-Path $Source 'TreeGit.exe'))){throw 'TreeGit.exe is missing'}
. (Join-Path $PSScriptRoot 'build/git-runtime.ps1')
if(-not $info.bundledGit){throw 'A self-contained Git runtime is required in the standard release'}
$treeGitRuntime=Test-TreeGitRuntime (Join-Path $Source 'git')
if($treeGitRuntime.inputLockSha256 -ne (Get-TreeGitHash (Join-Path $PSScriptRoot 'build/git-runtime.lock.json'))){throw 'Bundled Git does not match the pinned release components'}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$name='TreeGit-'+$info.version+'-Windows-x64.zip'
$archive=Join-Path $Destination $name
if(Test-Path -LiteralPath $archive){throw 'This versioned archive already exists; choose a new version or output directory'}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$partial=$archive+'.partial-'+[Guid]::NewGuid().ToString('N')
$checksum=Join-Path $Destination 'SHA256SUMS.txt'
$partialChecksum=$checksum+'.partial-'+[Guid]::NewGuid().ToString('N')
$published=$false
try {
$zip=[IO.Compression.ZipFile]::Open($partial,[IO.Compression.ZipArchiveMode]::Create)
try {
    # Only program files from the build output. Never package preferences, cache or credentials.
    $programFiles=@('TreeGit.exe','LICENSE','THIRD-PARTY-LICENSES.md','使用说明.md','MCP-使用说明.md','mcp-readonly.json','mcp-readwrite.json','build-info.json')
    foreach($file in Get-ChildItem -LiteralPath $Source -File){
        if($file.Name -notin $programFiles -and $file.Extension -ne '.dll'){continue}
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,('TreeGit/'+$file.Name),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $zip.CreateEntry('TreeGit/data/') | Out-Null
    $treeGitRuntimeRoot=Join-Path $Source 'git'
    foreach($entry in $treeGitRuntime.files){
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $treeGitRuntimeRoot $entry.path),('TreeGit/git/'+$entry.path),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $treeGitRuntimeRoot 'runtime-manifest.json'),'TreeGit/git/runtime-manifest.json',[IO.Compression.CompressionLevel]::Optimal) | Out-Null
} finally {$zip.Dispose()}
$hash=Get-TreeGitHash $partial
[IO.File]::WriteAllText($partialChecksum,($hash+'  '+$name+"`n"),[Text.UTF8Encoding]::new($false))
[IO.File]::Move($partial,$archive)
$published=$true
if(Test-Path -LiteralPath $checksum){[IO.File]::Replace($partialChecksum,$checksum,$null)}
else{[IO.File]::Move($partialChecksum,$checksum)}
Write-Output $archive
} catch {
    if($published -and (Test-Path -LiteralPath $archive)){Remove-Item -LiteralPath $archive -Force}
    throw
} finally {
    foreach($temporary in @($partial,$partialChecksum)){if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}
}
