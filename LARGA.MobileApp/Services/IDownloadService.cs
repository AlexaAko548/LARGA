using System;
using System.Threading.Tasks;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Where a download landed, and how to open it from here if the platform supports that (null when it
/// doesn't apply - e.g. the share sheet already offered "open with" as part of saving it).
/// </summary>
public sealed record DownloadResult(string DisplayPath, Func<Task>? OpenAsync);

/// <summary>
/// Saves an exported file straight into the device's Downloads, without asking the driver where to put it
/// (test gap: export made the driver choose a save location instead of just downloading).
/// </summary>
public interface IDownloadService
{
    /// <summary>Writes <paramref name="content"/> under <paramref name="fileName"/>.</summary>
    Task<DownloadResult> SaveToDownloadsAsync(string fileName, byte[] content, string contentType);
}
