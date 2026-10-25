using System;
using System.IO;
using System.Threading.Tasks;

namespace LARGA.MobileApp.Services;

/// <summary>Shrinks a camera photo to a JPEG that fits storage.rules' 5 MB photo limit, rotated
/// upright from its EXIF orientation.</summary>
public static class PhotoResizer
{
    public static Task<string> ResizeToJpegAsync(string sourcePath, string targetPath, int maxDimension = 1280, int quality = 80)
    {
        return Task.Run(() =>
        {
#if ANDROID
            Android.Graphics.Bitmap? sampledBitmap = null;
            Android.Graphics.Bitmap? finalBitmap = null;
            Android.Graphics.Matrix? matrix = null;

            try
            {
                int rotationDegrees = 0;
                try
                {
                    var exif = new Android.Media.ExifInterface(sourcePath);
                    int orientation = exif.GetAttributeInt(Android.Media.ExifInterface.TagOrientation, 1);
                    rotationDegrees = orientation switch
                    {
                        6 => 90,
                        3 => 180,
                        8 => 270,
                        _ => 0
                    };
                }
                catch { }

                var options = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
                using (var boundsStream = File.OpenRead(sourcePath))
                {
                    Android.Graphics.BitmapFactory.DecodeStream(boundsStream, null, options);
                }

                int sampleSize = 1;
                int maxSourceDim = Math.Max(options.OutWidth, options.OutHeight);
                while (maxSourceDim / sampleSize > maxDimension)
                {
                    sampleSize *= 2;
                }

                options.InJustDecodeBounds = false;
                options.InSampleSize = sampleSize;

                using (var decodeStream = File.OpenRead(sourcePath))
                {
                    sampledBitmap = Android.Graphics.BitmapFactory.DecodeStream(decodeStream, null, options);
                }

                if (sampledBitmap == null) throw new Exception("Native stream decode failed.");

                finalBitmap = sampledBitmap;
                if (rotationDegrees != 0)
                {
                    matrix = new Android.Graphics.Matrix();
                    matrix.PostRotate(rotationDegrees);
                    finalBitmap = Android.Graphics.Bitmap.CreateBitmap(
                        sampledBitmap, 0, 0, sampledBitmap.Width, sampledBitmap.Height, matrix, true);
                }

                using (var fileStream = File.Create(targetPath))
                {
                    finalBitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, quality, fileStream);
                }

                return targetPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Native image processing error: {ex.Message}");
                using var stream = File.OpenRead(sourcePath);
                using var image = Microsoft.Maui.Graphics.Platform.PlatformImage.FromStream(stream);
                using var downsized = image.Downsize(maxDimension, true);
                using var outStream = File.Create(targetPath);
                downsized.Save(outStream, Microsoft.Maui.Graphics.ImageFormat.Jpeg, quality / 100f);

                return targetPath;
            }
            finally
            {
                if (finalBitmap != null && finalBitmap != sampledBitmap)
                {
                    finalBitmap.Recycle();
                    finalBitmap.Dispose();
                }
                if (sampledBitmap != null)
                {
                    sampledBitmap.Recycle();
                    sampledBitmap.Dispose();
                }
                matrix?.Dispose();
            }
#else
            using var stream = File.OpenRead(sourcePath);
            using var image = Microsoft.Maui.Graphics.Platform.PlatformImage.FromStream(stream);
            using var downsized = image.Downsize(maxDimension, true);
            using var outStream = File.Create(targetPath);
            downsized.Save(outStream, Microsoft.Maui.Graphics.ImageFormat.Jpeg, quality / 100f);
            return targetPath;
#endif
        });
    }
}
