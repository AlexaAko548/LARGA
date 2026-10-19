<#
Deploys ManagerWeb to Google Cloud Run.

  .\deploy\deploy-cloudrun.ps1

Needs the Google Cloud CLI (gcloud), signed in with an account that owns the larga-blmtaxi project:
  winget install Google.CloudSDK
  gcloud auth login
Docker is not needed: Cloud Build builds the image from the published output.

Settings, and why:
  - one instance (--max-instances 1) with session affinity: Blazor Server keeps each manager's
    session (circuit) and the sign-in ticket in memory, so every request must reach the same copy;
  - always one instance running, with CPU outside requests (--min-instances 1, --no-cpu-throttling):
    the SOS push to managers' phones (SosPushService) and the idle / late-return / maintenance /
    fuel background checks run inside this app. Scaled to zero, or with CPU only during page
    requests, they stop whenever nobody has the dashboard open - an SOS at night would be saved
    but never pushed (audit C-01). This costs a small always-on instance; set a budget alert;
  - 60-minute request timeout: Blazor's live connection reconnects by itself after that;
  - runs as the Firebase Admin SDK service account, the same identity the local key file uses,
    so Firestore, Auth, Storage and push behave exactly as they do on your PC - no key file uploaded.
#>
param(
    [string]$Project = "larga-blmtaxi",
    [string]$Region = "asia-southeast1",   # Singapore - closest Cloud Run region to the Philippines
    [string]$Service = "larga-managerweb"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $env:TEMP "larga-managerweb-cloudrun"

if (-not (Get-Command gcloud -ErrorAction SilentlyContinue)) {
    throw "gcloud isn't installed. Run: winget install Google.CloudSDK, then gcloud auth login"
}

Write-Host "Publishing ManagerWeb..." -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
# SharedCore is a MAUI-enabled library for the phone app; the web build doesn't need the MAUI workload.
dotnet publish "$root\LARGA.ManagerWeb\LARGA.ManagerWeb.csproj" -c Release -p:UseMauiEssentials=false -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if (-not (Test-Path "$out\wwwroot\firebase-auth.js")) {
    throw "wwwroot\firebase-auth.js is missing - copy firebase-auth.example.js to firebase-auth.js and fill in the web config first."
}
# Never ship local settings (they point at the service account key on this PC).
Remove-Item "$out\appsettings.Local.json", "$out\appsettings.Development.json" -ErrorAction SilentlyContinue
Copy-Item "$PSScriptRoot\Dockerfile" $out

Write-Host "Deploying to Cloud Run ($Service, $Region)..." -ForegroundColor Cyan
gcloud run deploy $Service `
    --project $Project `
    --region $Region `
    --source $out `
    --allow-unauthenticated `
    --service-account "firebase-adminsdk-fbsvc@$Project.iam.gserviceaccount.com" `
    --session-affinity `
    --timeout 3600 `
    --min-instances 1 `
    --no-cpu-throttling `
    --max-instances 1 `
    --memory 512Mi `
    --set-env-vars "Firestore__ProjectId=$Project"
if ($LASTEXITCODE -ne 0) { throw "gcloud run deploy failed" }

$url = gcloud run services describe $Service --project $Project --region $Region --format "value(status.url)"
Write-Host "ManagerWeb is live at $url" -ForegroundColor Green
