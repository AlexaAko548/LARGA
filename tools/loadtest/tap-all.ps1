<#
Taps the same button on every running emulator at (almost) the same moment.

  .\tap-all.ps1 -Text "Start Shift"     # LOAD-001: all 5 are waiting on "Confirm & Start Shift"
  .\tap-all.ps1 -Text "End Shift"       # LOAD-007: all 5 are waiting on "Confirm & End Shift"

It first finds the button on each screen (uiautomator), then taps them one right after the other
(well under a second apart), and prints the time to write in Start time / End time.
#>
param([Parameter(Mandatory=$true)][string]$Text)
. "$PSScriptRoot\common.ps1"

$serials = @(0..4 | ForEach-Object { Get-Serial $_ } | Where-Object { (Get-RunningSerials) -contains $_ })
$targets = @()
foreach ($s in $serials) {
    & $Adb -s $s shell uiautomator dump /sdcard/larga_ui.xml 2>$null | Out-Null
    $xml = (& $Adb -s $s shell cat /sdcard/larga_ui.xml) -join ''
    $pattern = '<node[^>]*(?:text|content-desc)="[^"]*' + [regex]::Escape($Text) + '[^"]*"[^>]*bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"'
    $m = [regex]::Match($xml, $pattern)
    if (-not $m.Success) { Write-Warning "$s : no '$Text' button on screen. Scroll it into view (or check the checklist/odometer/photo are done) and run again."; continue }
    $x = ([int]$m.Groups[1].Value + [int]$m.Groups[3].Value) / 2
    $y = ([int]$m.Groups[2].Value + [int]$m.Groups[4].Value) / 2
    $targets += [pscustomobject]@{ Serial=$s; X=[int]$x; Y=[int]$y }
}
if ($targets.Count -eq 0) { throw "Nothing to tap." }
if ($targets.Count -lt $serials.Count) {
    $ans = Read-Host "Only $($targets.Count) of $($serials.Count) emulators are ready. Tap those anyway? (y/n)"
    if ($ans -ne 'y') { return }
}
$started = Get-Date
foreach ($t in $targets) { & $Adb -s $t.Serial shell input tap $t.X $t.Y }
Write-Host ("Tapped '{0}' on {1} emulator(s) at {2:h:mm:ss tt}. Write this time in the sheet." -f $Text, $targets.Count, $started)
