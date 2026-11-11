<#
Moves all 5 emulators along roads in Cebu City at the same time, then brings them back to the garage.

  .\drive-routes.ps1 -ParkOnly          # put all 5 at the garage and keep them there (before clock-in, and for clock-out)
  .\drive-routes.ps1 -Minutes 30        # each driver does one lap: garage -> Cebu City stops -> garage, in about 30 minutes

Routes follow the actual roads: when the script starts it asks the free OSRM road-routing service
(router.project-osrm.org, needs internet) for the road path from the garage through each driver's stops.
Every stop is inside Cebu City; only the trip between the garage and the city goes outside it.
The road paths are saved in routes\ as A.road.gpx ... E.road.gpx for checking (open them on gpx.studio).
Your own GPX files saved as routes\A.gpx ... routes\E.gpx are used instead, if present.
If the routing service can't be reached, the script falls back to straight lines between the stops.
Stop any time with Ctrl+C (the emulators keep their last position).
#>
param(
    [double]$Minutes = 30,
    [switch]$ParkOnly,
    [double]$GarageLat = 0,
    [double]$GarageLon = 0,
    [string]$GpxFolder = "$PSScriptRoot\routes",
    [switch]$Straight,          # skip road routing and use straight lines between the stops
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

# Stops for each driver (lat, lon), all inside Cebu City. The router finds the roads between them.
$stops = @{
  'A' = @(@(10.31107,123.89336), @(10.31718,123.89137), @(10.30520,123.89580))   # Fuente Osmena, Capitol, Osmena Blvd (downtown)
  'B' = @(@(10.28165,123.88067), @(10.27830,123.88620), @(10.29230,123.90400))   # SM Seaside, Il Corso (SRP), Pier area
  'C' = @(@(10.31791,123.90516), @(10.32995,123.90646), @(10.32010,123.89800))   # Ayala Center, Cebu IT Park, Lahug/Gorordo
  'D' = @(@(10.29654,123.90075), @(10.29285,123.89881), @(10.31158,123.91838))   # Colon, Carbon Market, SM City Cebu
  'E' = @(@(10.33047,123.89874), @(10.32050,123.88200), @(10.31718,123.89137))   # JY Square (Lahug), Guadalupe, Capitol
}

function Read-Gpx([string]$path) {
    [xml]$x = Get-Content -Raw $path
    $pts = @($x.SelectNodes("//*[local-name()='trkpt' or local-name()='rtept']"))
    return @($pts | ForEach-Object { ,@([double]::Parse($_.lat, $Inv), [double]::Parse($_.lon, $Inv)) })
}

function Save-Gpx([string]$path, $points, [string]$name) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
    [void]$sb.AppendLine('<gpx version="1.1" creator="LARGA load test" xmlns="http://www.topografix.com/GPX/1/1"><trk><name>' + $name + '</name><trkseg>')
    foreach ($p in $points) { [void]$sb.AppendLine(('<trkpt lat="{0}" lon="{1}"/>' -f $p[0].ToString('0.000000', $Inv), $p[1].ToString('0.000000', $Inv))) }
    [void]$sb.AppendLine('</trkseg></trk></gpx>')
    Set-Content -Path $path -Value $sb.ToString() -Encoding UTF8
}

$script:RouteError = $null
$script:RoutingDown = $false
function Get-RoadRoute($waypoints) {
    # OSRM takes lon,lat pairs separated by ';'. Returns the road path as (lat, lon) points, or $null.
    # Tries the main OSRM demo server, then the OpenStreetMap Germany mirror.
    $coords = ($waypoints | ForEach-Object { '{0},{1}' -f $_[1].ToString('0.000000', $Inv), $_[0].ToString('0.000000', $Inv) }) -join ';'
    $bases = @('https://router.project-osrm.org/route/v1/driving/', 'https://routing.openstreetmap.de/routed-car/route/v1/driving/')
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue   # built into Windows 10/11; handles modern TLS
    $errors = @()
    foreach ($base in $bases) {
        $url = $base + $coords + '?overview=full&geometries=geojson'
        $json = $null
        try {
            if ($curl) {
                $json = (& $curl.Source -s -S --max-time 20 -A 'LARGA-loadtest/1.0 (student capstone project)' $url 2>&1) -join ''
                $r = $json | ConvertFrom-Json
            } else {
                $r = Invoke-RestMethod -Uri $url -TimeoutSec 20 -UseBasicParsing -UserAgent 'LARGA-loadtest/1.0 (student capstone project)'
            }
            if ($r.code -eq 'Ok' -and $r.routes) {
                return @($r.routes[0].geometry.coordinates | ForEach-Object { ,@([double]$_[1], [double]$_[0]) })
            }
            $errors += "$base answered: $($r.code) $($r.message)"
        } catch {
            $msg = $_.Exception.Message
            if ($json) { $msg = "$msg :: $($json.Substring(0, [Math]::Min(200, $json.Length)))" }
            $errors += "$base : $msg"
        }
    }
    $script:RouteError = $errors -join ' | '
    return $null
}

if (-not (Test-Path $GpxFolder)) { New-Item -ItemType Directory -Path $GpxFolder | Out-Null }
$garage = ,@($GarageLat, $GarageLon)
$tracks = @()
for ($i = 0; $i -lt 5; $i++) {
    $serial = Get-Serial $i
    if ($serials -notcontains $serial) { continue }
    $d = $Drivers[$i]
    $gpx = Join-Path $GpxFolder "$d.gpx"
    if (Test-Path $gpx) {
        $lap = $garage + (Read-Gpx $gpx) + $garage; $source = "your routes\$d.gpx"
    } else {
        # Go round the driver's city stops as many times as needed (up to 4) so the lap fills
        # about $Minutes at a normal city speed instead of finishing early.
        $useRoads = (-not $Straight) -and (-not $script:RoutingDown)
        $loops = 1; $lap = $null
        for ($attempt = 0; $attempt -lt 2; $attempt++) {
            $waypoints = $garage
            for ($n = 0; $n -lt $loops; $n++) { $waypoints += $stops[$d] }
            $waypoints += $garage
            $road = $null
            if ($useRoads) { $road = Get-RoadRoute $waypoints }
            if ($road -and $road.Count -ge 2) { $lap = $garage + $road + $garage; $source = 'roads' }
            else {
                $lap = $waypoints; $source = 'STRAIGHT LINES (road routing unavailable)'
                if ($useRoads) { Write-Warning "Driver ${d}: couldn't reach the road-routing service; using straight lines. Reason: $script:RouteError"; $useRoads = $false; $script:RoutingDown = $true }
            }
            $lapKm = 0.0
            for ($k = 1; $k -lt $lap.Count; $k++) { $lapKm += (Get-Meters $lap[$k-1][0] $lap[$k-1][1] $lap[$k][0] $lap[$k][1]) / 1000 }
            $wantKm = 22 * $Minutes / 60                                    # about 22 km/h city driving
            $need = [Math]::Min(4, [Math]::Ceiling($wantKm / [Math]::Max(0.5, $lapKm)))
            if ($attempt -eq 0 -and $need -gt 1) { $loops = $need } else { break }
        }
        if ($loops -gt 1) { $source += ", city stops x$loops" }
        if ($source -like 'roads*') {
            Save-Gpx (Join-Path $GpxFolder "$d.road.gpx") $lap "Driver $d"
            $source += " (saved as routes\$d.road.gpx)"
        }
    }
    $cum = @(0.0)
    for ($k = 1; $k -lt $lap.Count; $k++) { $cum += $cum[$k-1] + (Get-Meters $lap[$k-1][0] $lap[$k-1][1] $lap[$k][0] $lap[$k][1]) }
    $len = $cum[-1]
    $speed = $len / ($Minutes * 60)                                   # m/s so the lap takes about $Minutes
    $speed = [Math]::Min([Math]::Max($speed, 12/3.6), 60/3.6)          # keep it between 12 and 60 km/h
    $tracks += [pscustomobject]@{ Driver=$d; Serial=$serial; Lap=$lap; Cum=$cum; Len=$len; Speed=$speed; Done=$false; K=1 }
    Write-Host ("Driver {0} ({1}): {2:N1} km lap on {3}, {4:N0} km/h, about {5:N0} min" -f $d, $serial, ($len/1000), $source, ($speed*3.6), ($len/$speed/60))
}

$clock = [Diagnostics.Stopwatch]::StartNew()
$lastReport = -60
$finished = $false
Write-Host "Driving... a progress line appears every minute. Leave this window open (Ctrl+C stops it)."
try {
    while (@($tracks | Where-Object { -not $_.Done }).Count -gt 0) {
        $t = $clock.Elapsed.TotalSeconds
        foreach ($tr in $tracks) {
            if ($tr.Done) { Send-Fix $tr.Serial $GarageLat $GarageLon; continue }
            $dist = $tr.Speed * $t
            if ($dist -ge $tr.Len) { $tr.Done = $true; Send-Fix $tr.Serial $GarageLat $GarageLon; Write-Host "Driver $($tr.Driver) is back at the garage ($([int]($t/60)) min)."; continue }
            $k = [int]$tr.K
            while ($k -lt ($tr.Cum.Count - 1) -and $tr.Cum[$k] -lt $dist) { $k++ }
            $tr.K = $k
            $f = ($dist - $tr.Cum[$k-1]) / [Math]::Max(1e-6, ($tr.Cum[$k] - $tr.Cum[$k-1]))
            $f = [Math]::Min(1.0, [Math]::Max(0.0, $f))
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
    $finished = $true
} catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.InvocationInfo.PositionMessage -ForegroundColor Red
} finally {
    if (-not $finished) { Write-Host ("Stopped after {0:hh\:mm\:ss}. Run the same command again to restart the routes." -f $clock.Elapsed) -ForegroundColor Yellow }
}
if ($finished) { Hold-AtGarage "All drivers are back at the garage. They can clock out now (LOAD-007)." }
