# Combine PNG screenshots of a folder into one contact sheet (sheet.png)
param([string]$Dir, [int]$Cols = 4, [int]$CX = 0, [int]$CY = 0, [int]$CW = 320, [int]$CH = 240)
Add-Type -AssemblyName System.Drawing
$files = Get-ChildItem $Dir -Filter 'f*.png' | Sort-Object Name
$w = $CW; $h = $CH
$rows = [math]::Ceiling($files.Count / $Cols)
$sheet = New-Object System.Drawing.Bitmap ($w * $Cols), ($h * $rows)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::Black)
$font = New-Object System.Drawing.Font 'Arial', 9
$i = 0
foreach ($f in $files) {
  $img = [System.Drawing.Image]::FromFile($f.FullName)
  $x = ($i % $Cols) * $w; $y = [math]::Floor($i / $Cols) * $h
  $g.DrawImage($img, (New-Object System.Drawing.Rectangle $x, $y, $w, $h), (New-Object System.Drawing.Rectangle $CX, $CY, $w, $h), 'Pixel')
  $g.DrawString($f.BaseName, $font, [System.Drawing.Brushes]::Yellow, $x + 2, $y + $h - 14)
  $img.Dispose(); $i++
}
$sheet.Save((Join-Path $Dir 'sheet.png'))
