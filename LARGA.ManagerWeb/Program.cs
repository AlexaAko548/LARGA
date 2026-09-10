using System.IO;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using LARGA.ManagerWeb.Components;
using LARGA.SharedCore.Services;

var builder = WebApplication.CreateBuilder(args);

// Untracked, developer-local config overrides (Firestore service account path, etc.) -
// see appsettings.Local.json.example. Never commit the real appsettings.Local.json.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Server-side (admin) Firestore access for reporting/dashboard reads. Configure
// "Firestore:CredentialsPath" in appsettings.Development.json / an untracked
// appsettings.Local.json to point at a service account key (see LARGA.SeedTool/README.md
// for how to get one - the same key works here). Omit it to fall back to Application
// Default Credentials.
//
// Wrapped in Lazy<T> so a missing/misconfigured credential does NOT throw here - this
// factory runs the moment any component/service that depends on it is instantiated
// (e.g. as soon as Dashboard.razor is rendered), which is *before* that page's own
// try/catch around GetDashboardSnapshotAsync() gets a chance to run, and would otherwise
// crash the whole page with an unhandled exception. Deferring to Lazy<T> means the
// failure only happens - and only gets caught - when FleetReportingService actually
// reads .Value inside a method call.
builder.Services.AddSingleton(sp => new Lazy<FirestoreDb>(() =>
{
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    string projectId = config["Firestore:ProjectId"] ?? "larga-blmtaxi";
    string? credentialsPath = config["Firestore:CredentialsPath"];

    if (string.IsNullOrWhiteSpace(credentialsPath))
    {
        return FirestoreDb.Create(projectId);
    }

    if (!File.Exists(credentialsPath))
    {
        throw new FileNotFoundException(
            $"Firestore:CredentialsPath is set to '{credentialsPath}' but that file does not exist. " +
            "See LARGA.SeedTool/README.md for how to get a service account key.");
    }

    // GoogleCredential.FromFile is obsolete in favor of CredentialFactory, but for a
    // developer-supplied config path this is fine - same tradeoff as LARGA.SeedTool.
#pragma warning disable CS0618
    GoogleCredential credential = GoogleCredential.FromFile(credentialsPath);
#pragma warning restore CS0618
    FirestoreClient client = new FirestoreClientBuilder { GoogleCredential = credential }.Build();
    return FirestoreDb.Create(projectId, client);
}));

builder.Services.AddSingleton<FleetReportingService>();

// Firebase Admin SDK - lets ManagerWeb create/manage driver Auth accounts server-side
// (drivers never self-register; a manager provisions every driver login via the
// Driver & Shift Management page). Same credentials + same Lazy<T> deferral rationale
// as the FirestoreDb registration above.
builder.Services.AddSingleton(sp => new Lazy<FirebaseAuth>(() =>
{
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    string projectId = config["Firestore:ProjectId"] ?? "larga-blmtaxi";
    string? credentialsPath = config["Firestore:CredentialsPath"];

    AppOptions options = new() { ProjectId = projectId };
    if (!string.IsNullOrWhiteSpace(credentialsPath))
    {
        if (!File.Exists(credentialsPath))
        {
            throw new FileNotFoundException(
                $"Firestore:CredentialsPath is set to '{credentialsPath}' but that file does not exist. " +
                "See LARGA.SeedTool/README.md for how to get a service account key.");
        }

#pragma warning disable CS0618
        options.Credential = GoogleCredential.FromFile(credentialsPath);
#pragma warning restore CS0618
    }
    else
    {
        options.Credential = GoogleCredential.GetApplicationDefault();
    }

    FirebaseApp firebaseApp = FirebaseApp.Create(options, "LargaManagerWeb");
    return FirebaseAuth.GetAuth(firebaseApp);
}));

builder.Services.AddSingleton<DriverManagementService>();
builder.Services.AddSingleton<FinancialLedgerService>();
builder.Services.AddSingleton<GarageService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
