using Google.Cloud.Firestore;

namespace LARGA.Tests.Live;

/// <summary>GPS-006 on the emulator: closes ONE named test shift the way ShiftDeadlineService's 6:00 AM
/// auto-close does (status/isOnBreak/managerNote), then releases that shift's own locks as a clock-out
/// would. Only runs with LARGA_REMOTE_CLOSE_SHIFT=&lt;shift id&gt; and LARGA_REMOTE_CLOSE_DRIVER=&lt;driver uid&gt;.</summary>
public class LiveRemoteClose
{
    [Fact]
    public async Task CloseNamedTestShift()
    {
        string? shiftId = Environment.GetEnvironmentVariable("LARGA_REMOTE_CLOSE_SHIFT");
        string? driverId = Environment.GetEnvironmentVariable("LARGA_REMOTE_CLOSE_DRIVER");
        string? credentials = Environment.GetEnvironmentVariable("LARGA_CREDENTIALS");
        if (string.IsNullOrEmpty(shiftId) || string.IsNullOrEmpty(driverId) || !File.Exists(credentials)) return;

        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", null);
        FirestoreDb db = new FirestoreDbBuilder { ProjectId = "larga-blmtaxi", CredentialsPath = credentials, EmulatorDetection = Google.Api.Gax.EmulatorDetection.ProductionOnly }.Build();

        DocumentReference shift = db.Collection("shifts").Document(shiftId);
        DocumentSnapshot snap = await shift.GetSnapshotAsync();
        Assert.True(snap.Exists);
        Assert.Equal(driverId, snap.GetValue<string>("driverId"));
        Assert.Equal("Active", snap.GetValue<string>("status"));

        await shift.UpdateAsync(new Dictionary<string, object>
        {
            ["status"] = "Auto-Closed",
            ["isOnBreak"] = false,
            ["managerNote"] = "Alpha test GPS-006: closed remotely to test the driver app (not a real missed clock-out).",
        });

        foreach (string lockId in new[] { $"driver_{driverId}", $"unit_{snap.GetValue<string>("taxiId")}" })
        {
            DocumentReference lockRef = db.Collection("shift_locks").Document(lockId);
            DocumentSnapshot l = await lockRef.GetSnapshotAsync();
            if (l.Exists && l.TryGetValue("shiftId", out string? held) && held == shiftId)
            {
                await lockRef.UpdateAsync(new Dictionary<string, object> { ["released"] = true, ["updatedAt"] = FieldValue.ServerTimestamp });
            }
        }
    }
}
