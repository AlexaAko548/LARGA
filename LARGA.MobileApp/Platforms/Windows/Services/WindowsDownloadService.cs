using LARGA.MobileApp.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LARGA.MobileApp.Platforms.Windows.Services;

/// <summary>
/// Writes straight into the signed-in user's Downloads folder - no save-location picker. Plain
/// System.IO rather than Windows.Storage.KnownFolders: this app is unpackaged (WindowsPackageType=None),
/// and KnownFolders.DownloadsFolder isn't available to it (CS0117 in CI - that WinRT member needs a
/// contract this build doesn't have access to).
/// </summary>
public class WindowsDownloadService : IDownloadService
{
    public async Task<DownloadResult> SaveToDownloadsAsync(string fileName, byte[] content, string contentType)
    {
        string downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloadsDir);

        string fullPath = Path.Combine(downloadsDir, fileName);
        int counter = 1;
        while (File.Exists(fullPath))
        {
            fullPath = Path.Combine(downloadsDir, $"{Path.GetFileNameWithoutExtension(fileName)} ({counter++}){Path.GetExtension(fileName)}");
        }

        await File.WriteAllBytesAsync(fullPath, content);

        string savedName = Path.GetFileName(fullPath);
        return new DownloadResult($"Downloads/{savedName}", () => Launcher.Default.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(fullPath) }));
    }
}
