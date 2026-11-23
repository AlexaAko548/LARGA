namespace LARGA.MobileApp.Services;

/// <summary>How far to turn a camera photo to make it upright, from its EXIF orientation tag.
/// Phones store most photos sideways and only tag them; mirrored orientations aren't used by
/// phone cameras and are left as they are.</summary>
public static class PhotoOrientation
{
    /// <summary>Clockwise degrees for an EXIF orientation value (also SkiaSharp's SKEncodedOrigin,
    /// which uses the same numbers).</summary>
    public static int DegreesFromExif(int orientation) => orientation switch
    {
        6 => 90,
        3 => 180,
        8 => 270,
        _ => 0,
    };

#if ANDROID
    /// <summary>Clockwise degrees for the photo at <paramref name="path"/>; 0 when it has no
    /// readable EXIF tag.</summary>
    public static int ReadDegrees(string path)
    {
        try
        {
            var exif = new Android.Media.ExifInterface(path);
            return DegreesFromExif(exif.GetAttributeInt(Android.Media.ExifInterface.TagOrientation, 1));
        }
        catch
        {
            return 0;
        }
    }
#endif
}
