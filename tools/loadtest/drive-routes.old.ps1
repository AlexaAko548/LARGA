<#
Moves all 5 emulators along routes in Cebu City at the same time, then brings them back to the garage.

  .\drive-routes.ps1 -ParkOnly          # put all 5 at the garage and keep them there (before clock-in, and for clock-out)
  .\drive-routes.ps1 -Minutes 30        # each driver does one lap garage -> Cebu City -> garage in about 30 minutes

Routes: built-in waypoints below (straight lines between them, about 25-50 km/h).
For road-accurate routes, draw them on https://gpx.studio, export as GPX, and save as routes\A.gpx ... routes\E.gpx;
any file found there is used instead of the built-in route for that driver.
Stop any time with Ctrl+C (the emulators keep their last position).
#>
param(
    [double]$Minutes = 30,
    [switch]$ParkOnly,
    [double]$GarageLat = 0,
    [double]$GarageLon = 0,
    [string]$GpxFolder = "$PSScriptRoot\routes",
    [int]$TickSeconds = 2
)
. "$PSScriptRoot\common.ps1"
if ($GarageLat -eq 0) { $GarageLat = $DefaultGarageLat }
if ($GarageLon -eq 0) { $GarageLon = $DefaultGarageLon }

$running = @(Get-RunningSerials)
$serials = @(0..4 | ForEach-Object { Get-Serial $_ } | Where-Object { $running -contains $_ })
if ($serials.Count -eq 0) { throw "No emulators running. Start them with .\setup-emulators.ps1" }
Write-Host "Emulators found: $($serials -join ', ')"

function Hold-AtGarage([string]$message) {
    Write-Host $message
    Write-Host "Keeping all emulators at the garage ($GarageLat, $GarageLon). Press Ctrl+C to stop."
    while ($true) {
        foreach ($s in $serials) { Send-Fix $s $GarageLat $GarageLon }
        Start-Sleep -Seconds 10
    }
}
if ($ParkOnly) { Hold-AtGarage "Parking all drivers at the garage." }

# Approximate waypoints (lat, lon). The shared start follows N. Bacalso Ave from Talisay into Cebu City.
$toCity = @(@(10.2600,123.8640), @(10.2660,123.8655), @(10.2740,123.8700), @(10.2820,123.8745), @(10.2900,123.8800))
$builtIn = @{
  'A' = $toCity + @(@(10.2975,123.8920), @(10.3110,123.8930), @(10.3170,123.8913))                          # Fuente Osmena, Capitol
  'B' = @(@(10.2600,123.8640), @(10.2660,123.8655), @(10.2740,123.8700), @(10.2790,123.8790), @(10.2817,123.8807), @(10.2870,123.8900), @(10.2925,123.9055))  # SRP, SM Seaside, Pier area
  'C' = $toCity + @(@(10.2975,123.8920), @(10.3110,123.8930), @(10.3180,123.9055), @(10.3300,123.9055))   # Ayala Center, IT Park
  'D' = $toCity + @(@(10.2975,123.8920), @(10.2965,123.9005), @(10.3020,123.9090), @(10.3115,123.9185))   # Colon, SM City Cebu
  'E' = $toCity + @(@(10.2975,123.8920), @(10.3110,123.8930), @(10.3170,123.8913), @(10.3310,123.8985))   # Capitol, Lahug (JY Square)
}

function Read-Gpx([string]$path) {
    [xml]$x = Get-Content -Raw $path
    $pts = @($x.SelectNodes("//*[local-name()='trkpt' or local-name()='rtept']"))
    return @($pts | ForEach-Object { ,@([double]::Parse($_.lat, $Inv), [double]::Parse($_.lon, $Inv)) })
}

$tracks = @()
for ($i = 0; $i -lt 5; $i++) {
    $serial = Get-Serial $i
    if ($serials -notcontains $serial) { continue }
    $d = $Drivers[$i]
    $gpx = Join-Path $GpxFolder "$d.gpx"
    if (Test-Path $gpx) {
        $out = Read-Gpx $gpx; $source = "routes\$d.gpx"
        $lap = @(,@($GarageLat,$GarageLon)) + $out + @(,@($GarageLat,$GarageLon))
    } else {
        $out = $builtIn[$d]; $source = 'built-in'
        $back = @($out); [Array]::Reverse($back)
        $lap = @(,@($GarageLat,$GarageLon)) + $out + $back + @(,@($GarageLat,$GarageLon))
    }
    $cum = @(0.0)
    for ($k = 1; $k -lt $lap.Count; $k++) { $cum += $cum[$k-1] + (Get-Meters $lap[$k-1][0] $lap[$k-1][1] $lap[$k][0] $lap[$k][1]) }
    $len = $cum[-1]
    $speed = $len / ($Minutes * 60)                                   # m/s so the lap takes about $Minutes
    $speed = [Math]::Min([Math]::Max($speed, 25/3.6), 60/3.6)          # keep it between 25 and 60 km/h
    $tracks += [pscustomobject]@{ Driver=$d; Serial=$serial; Lap=$lap; Cum=$cum; Len=$len; Speed=$speed; Done=$false }
    Write-Host ("Driver {0} ({1}): {2:N1} km lap from {3}, {4:N0} km/h, about {5:N0} min" -f $d, $serial, ($len/1000), $source, ($speed*3.6), ($len/$speed/60))
}

$clock = [Diagnostics.Stopwatch]::StartNew()
$lastReport = -60
while (($tracks | Where-Object { -not $_.Done }).Count -gt 0) {
    $t = $clock.Elapsed.TotalSeconds
    foreach ($tr in $tracks) {
        if ($tr.Done) { Send-Fix $tr.Serial $GarageLat $GarageLon; continue }
        $dist = $tr.Speed * $t
        if ($dist -ge $tr.Len) { $tr.Done = $true; Send-Fix $tr.Serial $GarageLat $GarageLon; Write-Host "Driver $($tr.Driver) is back at the garage ($([int]($t/60)) min)."; continue }
        $k = 1; while ($tr.Cum[$k] -lt $dist) { $k++ }
        $f = ($dist - $tr.Cum[$k-1]) / [Math]::Max(1e-6, ($tr.Cum[$k] - $tr.Cum[$k-1]))
        $lat = $tr.Lap[$k-1][0] + ($tr.Lap[$k][0] - $tr.Lap[$k-1][0]) * $f
        $lon = $tr.Lap[$k-1][1] + ($tr.Lap[$k][1] - $tr.Lap[$k-1][1]) * $f
        Send-Fix $tr.Serial $lat $lon
    }
    if ($t - $lastReport -ge 60) {
        $lastReport = $t
        $status = ($tracks | ForEach-Object { "{0}:{1:P0}" -f $_.Driver, [Math]::Min(1.0, ($_.Speed*$t)/$_.Len) }) -join '  '
        Write-Host ("[{0:hh\:mm\:ss}] {1}" -f $clock.Elapsed, $status)
    }
    Start-Sleep -Seconds $TickSeconds
}
Hold-AtGarage "All drivers are back at the garage. They can clock out now (LOAD-007)."
