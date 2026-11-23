<#
Lets your laptop keyboard type into the 5 LARGA emulators (sets hw.keyboard=yes).
Close all 5 emulator windows first, run this, then start them again with .\setup-emulators.ps1
#>
foreach ($d in 'A','B','C','D','E') {
    $ini = Join-Path $env:USERPROFILE ".android\avd\LARGA_$d.avd\config.ini"
    if (-not (Test-Path $ini)) { Write-Warning "LARGA_$d not found ($ini)"; continue }
    $lines = @(Get-Content $ini)
    if ($lines -match '^hw\.keyboard=') { $lines = $lines -replace '^hw\.keyboard=.*', 'hw.keyboard=yes' } else { $lines += 'hw.keyboard=yes' }
    $lines | Set-Content $ini
    Write-Host "LARGA_$d : laptop keyboard enabled"
}
Write-Host "Now start the emulators again: .\setup-emulators.ps1"
