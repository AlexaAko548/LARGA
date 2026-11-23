<#
Creates (once) and starts 5 Android emulators for the LARGA load test, then installs the APK.

  .\setup-emulators.ps1 -Create                      # first time only: makes LARGA_A..LARGA_E
  .\setup-emulators.ps1 -Apk "C:\path\to\com.blmtaxi.larga.mobile-Signed.apk"   # start all 5 + install
  .\setup-emulators.ps1 -Only D,E                    # restart just drivers D and E (emulator-5560, 5562)

Each emulator is a separate AVD so logins and permissions survive a restart.
#>
param(
    [switch]$Create,
    [string]$Apk,
    [string]$SystemImage = '',   # empty = pick an installed Google Play x86_64 image automatically
    [string]$Device = 'pixel_5',
    [int]$RamMb = 2048,
    [string[]]$Only = @()   # e.g. -Only D,E to (re)start just those drivers
)
. "$PSScriptRoot\common.ps1"

if ($Create) {
    $avdmanager = Get-ChildItem -Path (Join-Path $Sdk 'cmdline-tools') -Recurse -Filter 'avdmanager.bat' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $avdmanager) { throw "avdmanager not found. Install 'Android SDK Command-line Tools' (Android SDK Manager), or create LARGA_A..E by hand in the Android Device Manager." }
    if (-not $SystemImage) {
        # Prefer API 35, then 34, then the newest installed Google Play x86_64 image.
        $installed = Get-ChildItem (Join-Path $Sdk 'system-images') -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'google_apis_playstore\x86_64') } | ForEach-Object { $_.Name }
        $pick = @('android-35','android-34') | Where-Object { $installed -contains $_ } | Select-Object -First 1
        if (-not $pick) { $pick = $installed | Sort-Object -Descending | Select-Object -First 1 }
        if (-not $pick) { throw "No Google Play x86_64 system image installed. Install one in Visual Studio: Tools > Android > Android SDK Manager." }
        $SystemImage = "system-images;$pick;google_apis_playstore;x86_64"
    }
    Write-Host "Using system image: $SystemImage"
    $failed = 0
    foreach ($d in $Drivers) {
        Write-Host "Creating LARGA_$d ..."
        $out = 'no' | & $avdmanager.FullName create avd -n "LARGA_$d" -k $SystemImage -d $Device --force 2>&1
        if ($LASTEXITCODE -ne 0 -or ($out -match 'Error')) { $failed++; Write-Warning (($out | Out-String).Trim()); continue }
        $ini = Join-Path $env:USERPROFILE ".android\avd\LARGA_$d.avd\config.ini"
        if (Test-Path $ini) {
            # RAM size, and let the laptop keyboard type into the emulator (hw.keyboard).
            foreach ($kv in @(@('hw.ramSize', "$RamMb"), @('hw.keyboard', 'yes'))) {
                $lines = @(Get-Content $ini)
                if ($lines -match "^$([regex]::Escape($kv[0]))=") { $lines = $lines -replace "^$([regex]::Escape($kv[0]))=.*", "$($kv[0])=$($kv[1])" }
                else { $lines += "$($kv[0])=$($kv[1])" }
                $lines | Set-Content $ini
            }
        }
    }
    if ($failed -gt 0) { Write-Warning "$failed of 5 emulators were not created. Read the error above." }
    else { Write-Host "Done. LARGA_A to LARGA_E were created. Next: .\setup-emulators.ps1 -Apk <path to your APK>" }
    return
}

$which = @(0..4 | Where-Object { $Only.Count -eq 0 -or ($Only -contains $Drivers[$_]) })
if ($which.Count -eq 0) { throw "-Only must list driver letters A to E, e.g. -Only D,E" }
$running = @(Get-RunningSerials)
foreach ($i in $which) {
    $port = 5554 + 2 * $i
    if ($running -contains (Get-Serial $i)) { Write-Host "LARGA_$($Drivers[$i]) (emulator-$port) is already running."; continue }
    Write-Host "Starting LARGA_$($Drivers[$i]) on port $port ..."
    Start-Process -FilePath $Emulator -ArgumentList @('-avd', "LARGA_$($Drivers[$i])", '-port', "$port", '-no-boot-anim', '-gpu', 'host', '-no-snapshot-save') -WindowStyle Normal
    Start-Sleep -Seconds 8   # stagger so the laptop isn't hit by 5 cold boots at once
}

Write-Host "Waiting for them to finish booting (can take a few minutes) ..."
foreach ($i in $which) {
    $s = Get-Serial $i
    & $Adb -s $s wait-for-device | Out-Null
    do { Start-Sleep 3; $boot = (& $Adb -s $s shell getprop sys.boot_completed 2>$null) } while ($boot -notmatch '1')
    Write-Host "  $s (driver $($Drivers[$i])) is ready"
}

if ($Apk) {
    if (-not (Test-Path $Apk)) { throw "APK not found: $Apk" }
    foreach ($i in $which) {
        $s = Get-Serial $i
        Write-Host "Installing on $s ..."
        & $Adb -s $s install -r -d -g $Apk   # -r replace, -d allow an older version, -g grant permissions
    }
}
Write-Host "Emulators are up. Next: log in each as Test Driver A..E, then run .\drive-routes.ps1 -ParkOnly"
