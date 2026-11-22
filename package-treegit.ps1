param([Parameter(Mandatory=$true)][string]$Source, [string]$Destination=$PSScriptRoot, [switch]$WithAppUpdate)
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
$name='TreeGit-'+$info.version+'-Windows-x64-BundledGit.zip'
$archive=Join-Path $Destination $name
if(Test-Path -LiteralPath $archive){throw 'This versioned archive already exists; choose a new version or output directory'}
$appName=if($WithAppUpdate){'TreeGit-'+$info.version+'-Windows-x64-SystemGit.zip'}
$appArchive=if($WithAppUpdate){Join-Path $Destination $appName}
if($WithAppUpdate -and (Test-Path -LiteralPath $appArchive)){throw 'This application update archive already exists'}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$partial=$archive+'.partial-'+[Guid]::NewGuid().ToString('N')
$runtimeArchive=$partial+'.git'
$partialApp=$partial+'.app'
$identityFile=Join-Path $Destination 'BundledGit-Identity.txt'
$partialIdentity=$identityFile+'.partial-'+[Guid]::NewGuid().ToString('N')
if(Test-Path -LiteralPath $identityFile){throw 'A bundled Git identity already exists in the output directory'}
$checksum=Join-Path $Destination 'SHA256SUMS.txt'
$partialChecksum=$checksum+'.partial-'+[Guid]::NewGuid().ToString('N')
# Modern .NET supports denser standard ZIP entries. Windows PowerShell 5 uses its best available setting.
$compression=[IO.Compression.CompressionLevel]::Optimal
if([Enum]::IsDefined([IO.Compression.CompressionLevel],'SmallestSize')){
    $compression=[Enum]::Parse([IO.Compression.CompressionLevel],'SmallestSize')
}
$published=$false
$appPublished=$false
$identityPublished=$false
function Add-TreeGitProgramFiles([IO.Compression.ZipArchive]$Zip) {
    # Only program files from the build output. Never package preferences, cache or credentials.
    $programFiles=@('TreeGit.exe','LICENSE','THIRD-PARTY-LICENSES.md','使用说明.md','MCP-使用说明.md','build-info.json')
    foreach($file in Get-ChildItem -LiteralPath $Source -File){
        if($file.Name -notin $programFiles -and $file.Extension -ne '.dll'){continue}
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($Zip,$file.FullName,('TreeGit/'+$file.Name),$compression) | Out-Null
    }
    # Installed MCP files may contain personal commands, paths or secrets.
    # Package fresh examples without reading or overwriting those files.
    foreach($readOnly in @($true,$false)){
        $exampleArgs=if($readOnly){@('--mcp','--read-only')}else{@('--mcp')}
        $exampleName=if($readOnly){'mcp-readonly.json'}else{'mcp-readwrite.json'}
        $example=@{mcpServers=@{treegit=@{command='C:\Tools\TreeGit\TreeGit.exe';args=@($exampleArgs)}}}
        $entry=$Zip.CreateEntry(('TreeGit/'+$exampleName),$compression)
        $writer=[IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($false))
        try{$writer.WriteLine(($example | ConvertTo-Json -Depth 6))}finally{$writer.Dispose()}
    }
    $Zip.CreateEntry('TreeGit/data/') | Out-Null
}
try {
$zip=[IO.Compression.ZipFile]::Open($partial,[IO.Compression.ZipArchiveMode]::Create)
try {
    Add-TreeGitProgramFiles $zip
    $treeGitRuntimeRoot=Join-Path $Source 'git'
    $inner=[IO.Compression.ZipFile]::Open($runtimeArchive,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($file in $treeGitRuntime.files){
            $entry=$inner.CreateEntry($file.path,$compression)
            $entry.LastWriteTime=[DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
            $inputFile=[IO.File]::OpenRead((Join-Path $treeGitRuntimeRoot $file.path))
            try {
                $outputFile=$entry.Open()
                try{$inputFile.CopyTo($outputFile)}finally{$outputFile.Dispose()}
            } finally {$inputFile.Dispose()}
        }
    } finally {$inner.Dispose()}
    # The previous updater checks the real Windows launcher and a flat hash manifest.
    $payloadFiles=[ordered]@{'cmd/git.exe'=(Join-Path $treeGitRuntimeRoot 'cmd/git.exe');'payload-manifest.json'=(Join-Path $treeGitRuntimeRoot 'runtime-manifest.json');'runtime.zip'=$runtimeArchive}
    foreach($notice in @('COMPONENTS.md','LICENSE.txt','licenses/Git-LFS-LICENSE.md','licenses/Git-Flow-LICENSE')){
        $payloadFiles[$notice]=Join-Path $treeGitRuntimeRoot $notice
    }
    $records=foreach($relative in $payloadFiles.Keys){
        $path=$payloadFiles[$relative]
        $level=if($relative -eq 'runtime.zip'){[IO.Compression.CompressionLevel]::NoCompression}else{$compression}
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$path,('TreeGit/git/'+$relative),$level) | Out-Null
        @{path=$relative;sha256=(Get-TreeGitHash $path)}
    }
    $entry=$zip.CreateEntry('TreeGit/git/runtime-manifest.json',$compression)
    $writer=[IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($false))
    try{$writer.Write((@{schema=1;files=@($records)}|ConvertTo-Json -Depth 4 -Compress))}finally{$writer.Dispose()}
} finally {$zip.Dispose()}
$hash=Get-TreeGitHash $partial
$checksums=$hash+'  '+$name+"`n"
if($WithAppUpdate){
    $appZip=[IO.Compression.ZipFile]::Open($partialApp,[IO.Compression.ZipArchiveMode]::Create)
    try { Add-TreeGitProgramFiles $appZip } finally { $appZip.Dispose() }
    $checksums+=(Get-TreeGitHash $partialApp)+'  '+$appName+"`n"
    [IO.File]::Move($partialApp,$appArchive)
    $appPublished=$true
}
[IO.File]::WriteAllText($partialIdentity,($treeGitRuntime.gitVersion+"`n"+(Get-TreeGitRuntimeFingerprint $treeGitRuntime)+"`n"),[Text.UTF8Encoding]::new($false))
$checksums+=(Get-TreeGitHash $partialIdentity)+'  '+(Split-Path -Leaf $identityFile)+"`n"
[IO.File]::Move($partialIdentity,$identityFile)
$identityPublished=$true
[IO.File]::WriteAllText($partialChecksum,$checksums,[Text.UTF8Encoding]::new($false))
[IO.File]::Move($partial,$archive)
$published=$true
if(Test-Path -LiteralPath $checksum){[IO.File]::Replace($partialChecksum,$checksum,[NullString]::Value)}
else{[IO.File]::Move($partialChecksum,$checksum)}
Write-Output $archive
if($WithAppUpdate){Write-Output $appArchive}
} catch {
    if($published -and (Test-Path -LiteralPath $archive)){Remove-Item -LiteralPath $archive -Force}
    if($appPublished -and (Test-Path -LiteralPath $appArchive)){Remove-Item -LiteralPath $appArchive -Force}
    if($identityPublished -and (Test-Path -LiteralPath $identityFile)){Remove-Item -LiteralPath $identityFile -Force}
    throw
} finally {
    foreach($temporary in @($partial,$partialChecksum,$runtimeArchive,$partialApp,$partialIdentity)){if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}
}
