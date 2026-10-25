using LARGA.MobileApp.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using System.Threading.Tasks;
using Windows.Storage;

namespace LARGA.MobileApp.Platforms.Windows.Services;

/// <summary>Writes straight into the signed-in user's Downloads folder - no save-location picker.</summary>
public class WindowsDownloadService : IDownloadService
{
    public async Task<DownloadResult> SaveToDownloadsAsync(string fileName, byte[] content, string contentType)
    {
        StorageFolder downloads = KnownFolders.DownloadsFolder;
        StorageFile file = await downloads.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);
        await FileIO.WriteBytesAsync(file, content);

        string fullPath = file.Path;
        return new DownloadResult($"Downloads/{file.Name}", () => Launcher.Default.OpenAsync(new OpenFileRequest(new ReadOnlyFile(fullPath))));
    }
}
