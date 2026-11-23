# Shared helpers for the LARGA load-test scripts (PowerShell 5.1+ on Windows).
$ErrorActionPreference = 'Continue'   # adb writes progress to stderr; don't treat it as fatal
$Inv = [System.Globalization.CultureInfo]::InvariantCulture

function Get-SdkRoot {
    foreach ($p in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, "$env:LOCALAPPDATA\Android\Sdk", "C:\Program Files (x86)\Android\android-sdk")) {
        if ($p -and (Test-Path (Join-Path $p 'platform-tools\adb.exe'))) { return $p }
    }
    throw "Android SDK not found. Set ANDROID_HOME to your SDK folder (Visual Studio: Tools > Options > Xamarin/.NET MAUI > Android Settings)."
}
$Sdk = Get-SdkRoot
$Adb = Join-Path $Sdk 'platform-tools\adb.exe'
$Emulator = Join-Path $Sdk 'emulator\emulator.exe'

# Emulator serials: emulator-5554, 5556, 5558, 5560, 5562 = drivers A..E
$Drivers = @('A','B','C','D','E')
function Get-Serial([int]$i) { "emulator-$((5554 + 2 * $i))" }

function Get-RunningSerials {
    (& $Adb devices) | Where-Object { $_ -match '^(emulator-\d+)\s+device' } | ForEach-Object { $Matches[1] }
}

# Default garage pin = ShiftRules.GarageLatitude/Longitude (radius 150 m).
# If BLM's web settings use a different pin (system_configs/global), pass -GarageLat / -GarageLon.
$DefaultGarageLat = 10.253286005
$DefaultGarageLon = 123.861463669

function Send-Fix([string]$serial, [double]$lat, [double]$lon) {
    # NB: adb geo fix takes LONGITUDE first.
    & $Adb -s $serial emu geo fix $lon.ToString('0.000000', $Inv) $lat.ToString('0.000000', $Inv) | Out-Null
}

function Get-Meters([double]$lat1, [double]$lon1, [double]$lat2, [double]$lon2) {
    $r = 6371000.0
    $dLat = ($lat2 - $lat1) * [Math]::PI / 180
    $dLon = ($lon2 - $lon1) * [Math]::PI / 180
    $a = [Math]::Sin($dLat/2) * [Math]::Sin($dLat/2) + [Math]::Cos($lat1*[Math]::PI/180) * [Math]::Cos($lat2*[Math]::PI/180) * [Math]::Sin($dLon/2) * [Math]::Sin($dLon/2)
    return 2 * $r * [Math]::Atan2([Math]::Sqrt($a), [Math]::Sqrt(1 - $a))
}
