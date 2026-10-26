using Android.Content;
using Android.OS;
using Android.Provider;
using LARGA.MobileApp.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LARGA.MobileApp.Platforms.Android.Services;

/// <summary>
/// Writes straight into the public Downloads collection via MediaStore (Android 10+ / API 29+) - no
/// WRITE_EXTERNAL_STORAGE permission needed, and no share-sheet picker: the file is just there, the same
/// way a browser download behaves.
/// </summary>
public class AndroidDownloadService : IDownloadService
{
    public async Task<DownloadResult> SaveToDownloadsAsync(string fileName, byte[] content, string contentType)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Q)
        {
            // Pre-Android 10 has no permissionless public-Downloads API; writing there needs
            // WRITE_EXTERNAL_STORAGE (not declared - see AndroidManifest.xml) and a runtime grant.
            // Falls back to the share sheet rather than failing the export outright.
            return await new ShareDownloadService().SaveToDownloadsAsync(fileName, content, contentType);
        }

        Context context = global::Android.App.Application.Context;
        ContentResolver? resolver = context.ContentResolver;
        if (resolver is null)
        {
            throw new IOException("Couldn't reach the device's storage.");
        }

        using var values = new ContentValues();
        values.Put(MediaStore.MediaColumns.DisplayName, fileName);
        values.Put(MediaStore.MediaColumns.MimeType, contentType);
        values.Put(MediaStore.MediaColumns.RelativePath, global::Android.OS.Environment.DirectoryDownloads);
        values.Put(MediaStore.MediaColumns.IsPending, 1);

        global::Android.Net.Uri? collection = MediaStore.Downloads.GetContentUri(MediaStore.VolumeExternal);
        global::Android.Net.Uri? item = collection is null ? null : resolver.Insert(collection, values);
        if (item is null)
        {
            throw new IOException("Couldn't create the download.");
        }

        using (Stream? output = resolver.OpenOutputStream(item))
        {
            if (output is null)
            {
                throw new IOException("Couldn't open the download for writing.");
            }
            await output.WriteAsync(content, 0, content.Length);
        }

        using var clearPending = new ContentValues();
        clearPending.Put(MediaStore.MediaColumns.IsPending, 0);
        resolver.Update(item, clearPending, null, null);

        Task OpenAsync()
        {
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(item, contentType);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            context.StartActivity(intent);
            return Task.CompletedTask;
        }

        return new DownloadResult($"Downloads/{fileName}", OpenAsync);
    }
}
