# Random-input coverage runs: collects unrecompiled addresses into seeds.txt and rebuilds
param([int]$Runs = 6, [int]$Frames = 40000, [int]$Base = 0)
$root = $PSScriptRoot
for ($i = 0; $i -lt $Runs; $i++) {
  Remove-Item "$root\bin\missing.txt","$root\bin\missing_z80.txt" -ErrorAction SilentlyContinue
  & "$root\bin\RR3_console.exe" --frames $Frames --random ($Base + $i) --shot 4000 --out "$root\bin\fuzz$($Base + $i)" | Out-String | Write-Host
  foreach ($f in @('missing.txt','missing_z80.txt')) {
    if (Test-Path "$root\bin\$f") {
      $dst = if ($f -eq 'missing.txt') { "$root\seeds.txt" } else { "$root\seeds_z80.txt" }
      Get-Content "$root\bin\$f" | Add-Content $dst
      Write-Host "run $i : new seeds from $f"; Get-Content "$root\bin\$f"
    }
  }
}
