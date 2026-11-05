# Shared by the portable builder and packager. Windows PowerShell 5 compatible.
function Get-TreeGitHash([string]$Path) {
    $algorithm=[Security.Cryptography.SHA256]::Create()
    $stream=[IO.File]::OpenRead($Path)
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}

function Test-TreeGitRuntime([string]$Path) {
    $root=[IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $manifestPath=Join-Path $root 'runtime-manifest.json'
    if(-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)){throw 'Bundled Git runtime manifest is missing; build into a fresh directory'}
    if((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked Git runtime directories are not allowed'}
    $items=@(Get-ChildItem -LiteralPath $root -Recurse -Force)
    if(@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count){throw 'Linked Git runtime files are not allowed'}
    $manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if($manifest.schema -ne 1 -or -not $manifest.files.Count){throw 'Invalid bundled Git manifest'}
    $actual=@($items | Where-Object { -not $_.PSIsContainer -and $_.FullName -ne $manifestPath })
    if($actual.Count -ne $manifest.files.Count){throw 'Unexpected or missing files in bundled Git runtime'}
    $seen=@{}
    foreach($entry in $manifest.files){
        $file=[IO.Path]::GetFullPath((Join-Path $root $entry.path))
        if(-not $file.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($file)){throw 'Unsafe or duplicate Git manifest path'}
        $seen[$file]=$true
        if(-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-TreeGitHash $file) -ne $entry.sha256){throw ('Bundled Git file verification failed: '+$entry.path)}
    }
    foreach($required in @('cmd/git.exe','usr/bin/ssh.exe','ucrt64/libexec/git-core/git-lfs.exe','ucrt64/libexec/git-core/git-flow.exe','etc/gitconfig','ucrt64/etc/ssl/certs/ca-bundle.crt','LICENSE.txt','licenses/Git-LFS-LICENSE.md','licenses/Git-Flow-LICENSE','COMPONENTS.md')){
        if(-not $seen.ContainsKey([IO.Path]::GetFullPath((Join-Path $root $required)))){throw ('Required Git component is missing: '+$required)}
    }
    return $manifest
}

function Initialize-TreeGitRuntime([string]$Destination,[string]$CacheDirectory,[string]$LockFile,[string]$Notices) {
    $root=[IO.Path]::GetFullPath($Destination)
    $lock=Get-Content -LiteralPath $LockFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $lockHash=Get-TreeGitHash $LockFile
    if(Test-Path -LiteralPath $root){
        $existing=Test-TreeGitRuntime $root
        if($existing.inputLockSha256 -ne $lockHash){throw 'Bundled Git versions changed; choose a fresh build directory'}
        return $existing
    }
    $cache=[IO.Path]::GetFullPath($CacheDirectory)
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $cacheLock=[IO.File]::Open((Join-Path $cache 'runtime-cache.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        foreach($asset in $lock.assets){
            $file=Join-Path $cache $asset.name
            if(Test-Path -LiteralPath $file){
                if((Get-TreeGitHash $file) -ne $asset.sha256){throw ('Cached component checksum mismatch: '+$asset.name)}
                continue
            }
            $partial=$file+'.partial-'+[Guid]::NewGuid().ToString('N')
            $client=New-Object Net.WebClient
            try {
                [Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
                if($env:HTTPS_PROXY){$client.Proxy=New-Object Net.WebProxy($env:HTTPS_PROXY)}
                $client.Headers.Add('User-Agent','TreeGit-portable-build')
                $client.DownloadFile($asset.url,$partial)
                if((Get-TreeGitHash $partial) -ne $asset.sha256){throw ('Downloaded component checksum mismatch: '+$asset.name)}
                [IO.File]::Move($partial,$file)
            } finally {
                $client.Dispose()
                if(Test-Path -LiteralPath $partial){Remove-Item -LiteralPath $partial -Force}
            }
        }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $gitAsset=$lock.assets | Where-Object { $_.kind -eq 'git' }
        [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $cache $gitAsset.name),$root)
        foreach($asset in $lock.assets | Where-Object { $_.kind -ne 'git' }){
            $target=Join-Path $root $asset.target
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            if($asset.kind -eq 'file'){Copy-Item -LiteralPath (Join-Path $cache $asset.name) -Destination $target}
            else {
                $zip=[IO.Compression.ZipFile]::OpenRead((Join-Path $cache $asset.name))
                try {
                    $entry=$zip.GetEntry($asset.entry)
                    if(-not $entry){throw ('Required component archive entry is missing: '+$asset.entry)}
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$false)
                } finally {$zip.Dispose()}
            }
        }
        # Explicit path interpolation keeps libcurl CA resolution working in
        # Chinese/space directories. User/repository CA overrides still win.
        [IO.File]::AppendAllText((Join-Path $root 'etc/gitconfig'),"`n[http]`n`tsslCAInfo = %(prefix)/etc/ssl/certs/ca-bundle.crt`n",[Text.UTF8Encoding]::new($false))
        Copy-Item -LiteralPath $Notices -Destination (Join-Path $root 'COMPONENTS.md')
        $records=@(Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName | ForEach-Object {
            @{path=$_.FullName.Substring($root.Length+1).Replace('\','/');sha256=(Get-TreeGitHash $_.FullName)}
        })
        $manifest=@{schema=1;gitVersion=$lock.gitVersion;lfsVersion=$lock.lfsVersion;flowVersion=$lock.flowVersion;inputLockSha256=$lockHash;assets=$lock.assets;files=$records}
        [IO.File]::WriteAllText((Join-Path $root 'runtime-manifest.json'),($manifest|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
        return (Test-TreeGitRuntime $root)
    } finally {$cacheLock.Dispose()}
}
