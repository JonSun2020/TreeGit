param([string]$Before, [string]$After, [int]$Left, [int]$Top, [int]$Width, [int]$Height)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$beforeImage = [System.Drawing.Bitmap]::new($Before)
$afterImage = [System.Drawing.Bitmap]::new($After)
try {
    if ($Left -lt 0 -or $Top -lt 0 -or $Width -le 0 -or $Height -le 0 -or
        $Left + $Width -gt $beforeImage.Width -or $Top + $Height -gt $beforeImage.Height -or
        $beforeImage.Size -ne $afterImage.Size) { throw 'Invalid screenshot region' }
    for ($row = $Top; $row -lt $Top + $Height; $row++) {
        for ($column = $Left; $column -lt $Left + $Width; $column++) {
            if ($beforeImage.GetPixel($column, $row).ToArgb() -ne $afterImage.GetPixel($column, $row).ToArgb()) {
                throw "Group header changed while hovering its first branch: $column,$row"
            }
        }
    }
} finally {
    $beforeImage.Dispose()
    $afterImage.Dispose()
}
