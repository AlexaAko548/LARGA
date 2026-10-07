using Android.Gms.Extensions;
using Android.Graphics;
using Android.Media;
using Android.Runtime;
using LARGA.MobileApp.Services;
using Microsoft.Maui.Storage;
using System;
using System.IO;
using System.Threading.Tasks;
using Com.Google.Mlkit.Vision.Face;
using Xamarin.Google.MLKit.Vision.Common;

namespace LARGA.MobileApp.Platforms.Android.Services;

/// <summary>
/// Detects the driver's face on the license photo with ML Kit (on-device) and crops a square
/// around it. The crop is taken from the upright, full-resolution photo so the face keeps its
/// detail, then scaled down for the profile avatar.
/// </summary>
public class AndroidFaceCropService : IFaceCropService
{
    private const int OutputSizePx = 256;

    // Crop width as a multiple of the face's larger side, so the avatar keeps hair and the top
    // of the shoulders rather than cutting tight to the face.
    private const float ContextScale = 1.8f;

    public async Task<string?> CropDriverFaceAsync(string localFilePath)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
            return null;

        using var upright = await Task.Run(() => LoadUprightBitmap(localFilePath));
        if (upright is null)
            return null;

        var faceBox = await FindDriverFaceBoxAsync(upright);
        if (faceBox is null)
            return null;

        using var cropped = CropAroundFace(upright, faceBox);
        using var scaled = global::Android.Graphics.Bitmap.CreateScaledBitmap(cropped, OutputSizePx, OutputSizePx, true);

        string outputPath = System.IO.Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}_face.jpg");
        using (var stream = File.OpenWrite(outputPath))
        {
            scaled.Compress(global::Android.Graphics.Bitmap.CompressFormat.Jpeg!, 90, stream);
        }

        return outputPath;
    }

    private static global::Android.Graphics.Bitmap? LoadUprightBitmap(string path)
    {
        var raw = BitmapFactory.DecodeFile(path);
        if (raw is null)
            return null;

        // Camera photos are often stored sideways with an EXIF orientation tag. ML Kit reads
        // that tag for its own coordinates, so the crop has to be rotated the same way.
        int orientation = new ExifInterface(path).GetAttributeInt(ExifInterface.TagOrientation, 1);
        float degrees = orientation switch
        {
            6 => 90f,
            3 => 180f,
            8 => 270f,
            _ => 0f,
        };

        if (degrees == 0f)
            return raw;

        using var matrix = new Matrix();
        matrix.PostRotate(degrees);
        var rotated = global::Android.Graphics.Bitmap.CreateBitmap(raw, 0, 0, raw.Width, raw.Height, matrix, true);
        raw.Dispose();
        return rotated;
    }

    private static async Task<global::Android.Graphics.Rect?> FindDriverFaceBoxAsync(global::Android.Graphics.Bitmap upright)
    {
        var options = new FaceDetectorOptions.Builder()
            .SetPerformanceMode(FaceDetectorOptions.PerformanceModeAccurate)
            // Eye landmarks tell the real face apart from face-like shapes (see EyeCount).
            .SetLandmarkMode(FaceDetectorOptions.LandmarkModeAll)
            // A license photo is small: the face is a modest share of the frame, so the
            // detector needs to accept smaller faces than its default.
            .SetMinFaceSize(0.05f)
            .Build();

        var detector = FaceDetection.GetClient(options);
        try
        {
            using var image = InputImage.FromBitmap(upright, 0);
            var faces = (Java.Util.IList)await detector.Process(image);

            // A license has one face. Prefer the detection with the most eyes found, then the
            // larger one: the LTO seal on the card can be a bigger box than the small photo, so
            // "largest" alone picked the seal (ManagerWeb's face-api.js hit the same thing; it
            // picks by score, but ML Kit gives no score, so eye landmarks stand in for it).
            global::Android.Graphics.Rect? best = null;
            int bestEyes = -1;
            for (int i = 0; i < faces.Size(); i++)
            {
                if (faces.Get(i) is not { } face || FaceBoundingBox(face) is not { } box)
                    continue;

                int eyes = EyeCount(face);
                if (best is null || eyes > bestEyes || (eyes == bestEyes && Area(box) > Area(best)))
                {
                    best = box;
                    bestEyes = eyes;
                }
            }

            return best;
        }
        finally
        {
            detector.Close();
        }
    }

    // The NuGet binding doesn't expose ML Kit's Face class, so its getters are called directly
    // through JNI on the detected face object.
    private static global::Android.Graphics.Rect? FaceBoundingBox(Java.Lang.Object face)
    {
        IntPtr faceClass = JNIEnv.GetObjectClass(face.Handle);
        try
        {
            IntPtr method = JNIEnv.GetMethodID(faceClass, "getBoundingBox", "()Landroid/graphics/Rect;");
            IntPtr rectHandle = JNIEnv.CallObjectMethod(face.Handle, method);
            return Java.Lang.Object.GetObject<global::Android.Graphics.Rect>(rectHandle, JniHandleOwnership.TransferLocalRef);
        }
        finally
        {
            JNIEnv.DeleteLocalRef(faceClass);
        }
    }

    // ML Kit's FaceLandmark.LEFT_EYE / RIGHT_EYE. getLandmark returns null when the eye wasn't found.
    private const int LeftEyeLandmark = 4;
    private const int RightEyeLandmark = 10;

    private static int EyeCount(Java.Lang.Object face)
    {
        IntPtr faceClass = JNIEnv.GetObjectClass(face.Handle);
        try
        {
            IntPtr method = JNIEnv.GetMethodID(faceClass, "getLandmark", "(I)Lcom/google/mlkit/vision/face/FaceLandmark;");
            int count = 0;
            foreach (int landmark in new[] { LeftEyeLandmark, RightEyeLandmark })
            {
                IntPtr found = JNIEnv.CallObjectMethod(face.Handle, method, new JValue(landmark));
                if (found != IntPtr.Zero)
                {
                    count++;
                    JNIEnv.DeleteLocalRef(found);
                }
            }
            return count;
        }
        finally
        {
            JNIEnv.DeleteLocalRef(faceClass);
        }
    }

    private static global::Android.Graphics.Bitmap CropAroundFace(global::Android.Graphics.Bitmap upright, global::Android.Graphics.Rect faceBox)
    {
        float side = Math.Max(faceBox.Width(), faceBox.Height()) * ContextScale;
        side = Math.Min(side, Math.Min(upright.Width, upright.Height));

        // Keep the square inside the photo: shift it back in rather than letting it hang off an edge.
        int x = (int)Math.Clamp(faceBox.CenterX() - side / 2f, 0f, upright.Width - side);
        int y = (int)Math.Clamp(faceBox.CenterY() - side / 2f, 0f, upright.Height - side);
        int size = Math.Max(1, (int)side);

        return global::Android.Graphics.Bitmap.CreateBitmap(upright, x, y, size, size);
    }

    private static long Area(global::Android.Graphics.Rect box) => (long)box.Width() * box.Height();
}
