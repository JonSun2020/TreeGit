param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$ExpectedExecutable,
    [Parameter(Mandatory=$true)][int]$TargetX,
    [Parameter(Mandatory=$true)][int]$TargetY,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows'){throw 'Native capture requires the owned CI desktop'}
$expected=[IO.Path]::GetFullPath($ExpectedExecutable)
$target=[IO.Path]::GetFullPath($OutputPath)
$allowed=@($env:RUNNER_TEMP,$env:GITHUB_WORKSPACE)|Where-Object { $_ }|ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\')+'\' }
if(-not ($allowed|Where-Object {$target.StartsWith($_,[StringComparison]::OrdinalIgnoreCase)})){throw 'Capture output must stay in the owned CI workspace'}
$process=Get-Process -Id $ProcessId
if($process.Path -ne $expected){throw 'Refusing capture of an unexpected process'}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OwnedCaptureNative {
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
 [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window,uint flag);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window,out Rect rect);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
$null=[OwnedCaptureNative]::SetProcessDPIAware()
[uint32]$owner=0
$null=[OwnedCaptureNative]::GetWindowThreadProcessId([OwnedCaptureNative]::GetForegroundWindow(),[ref]$owner)
if($owner -ne $ProcessId){throw 'Owned application must remain in the foreground'}
$point=New-Object OwnedCaptureNative+Point;$point.X=$TargetX;$point.Y=$TargetY
$window=[OwnedCaptureNative]::GetAncestor([OwnedCaptureNative]::WindowFromPoint($point),2)
$null=[OwnedCaptureNative]::GetWindowThreadProcessId($window,[ref]$owner)
if($owner -ne $ProcessId){throw 'Popup does not belong to the owned application'}
$rect=New-Object OwnedCaptureNative+Rect
if(-not [OwnedCaptureNative]::GetWindowRect($window,[ref]$rect)){throw 'Could not read popup bounds'}
$width=$rect.Right-$rect.Left;$height=$rect.Bottom-$rect.Top
if($width -lt 8 -or $height -lt 8 -or $width -gt 4096 -or $height -gt 4096){throw 'Unexpected popup dimensions'}
Add-Type -AssemblyName System.Drawing
$bitmap=New-Object System.Drawing.Bitmap($width,$height)
$graphics=[Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bitmap.Size)
    $bitmap.Save($target,[Drawing.Imaging.ImageFormat]::Png)
} finally { $graphics.Dispose();$bitmap.Dispose() }
@{owner=$owner;width=$width;height=$height;x=$rect.Left;y=$rect.Top;path=$target}|ConvertTo-Json -Compress
