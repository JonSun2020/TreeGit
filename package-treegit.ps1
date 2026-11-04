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
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try {
    # Only program files from the build output. Never package preferences, cache or credentials.
    foreach($file in Get-ChildItem -LiteralPath $Source -File){
        if($file.Extension -in @('.pdb','.zip')){continue}
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,('TreeGit/'+$file.Name),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $zip.CreateEntry('TreeGit/data/') | Out-Null
} finally {$zip.Dispose()}
$hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $Destination 'SHA256SUMS.txt'),($hash+'  '+$name+"`n"),[Text.UTF8Encoding]::new($false))
Write-Output $archive
