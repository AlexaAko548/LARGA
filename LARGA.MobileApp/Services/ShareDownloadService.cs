using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;
using System.IO;
using System.Threading.Tasks;

namespace LARGA.MobileApp.Services;

/// <summary>
/// Platforms with no permissionless "save straight to Downloads" API (iOS/MacCatalyst/Tizen, and Android
/// below 10 - see AndroidDownloadService) fall back to the OS share sheet, so the driver can still save it
/// somewhere even without a one-tap silent download.
/// </summary>
public class ShareDownloadService : IDownloadService
{
    public async Task<DownloadResult> SaveToDownloadsAsync(string fileName, byte[] content, string contentType)
    {
        string localPath = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllBytesAsync(localPath, content);

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = "Save file",
            File = new ShareFile(localPath, contentType),
        });

        // The share sheet already offered every app that can open this file - nothing further to do.
        return new DownloadResult(fileName, null);
    }
}
