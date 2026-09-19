using Android.Gms.Extensions;
using Android.Graphics;
using Android.Media;
using LARGA.MobileApp.Services;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xamarin.Google.MLKit.Vision.Common;
using Xamarin.Google.MLKit.Vision.Text;
using Xamarin.Google.MLKit.Vision.Text.Latin;

namespace LARGA.MobileApp.Platforms.Android.Services;

public class AndroidOcrService : IOcrService
{
    private readonly ITextRecognizer _recognizer;

    public AndroidOcrService()
    {
        _recognizer = TextRecognition.GetClient(new TextRecognizerOptions.Builder().Build());
    }

    public async Task<List<OcrTextBlock>> ExtractTextBlocksAsync(byte[] imageBytes)
    {
        using var bitmap = await BitmapFactory.DecodeByteArrayAsync(imageBytes, 0, imageBytes.Length);
        if (bitmap == null) return new List<OcrTextBlock>();

        int rotationDegrees = 0;
        try
        {
            using var ms = new MemoryStream(imageBytes);
            var exif = new ExifInterface(ms);

            // THE FIX: Use standard EXIF integers to bypass C# binding errors
            // 1 = Normal, 6 = Rotate90, 3 = Rotate180, 8 = Rotate270
            int orientation = exif.GetAttributeInt(ExifInterface.TagOrientation, 1);
            rotationDegrees = orientation switch
            {
                6 => 90,
                3 => 180,
                8 => 270,
                _ => 0
            };
        }
        catch { }

        double imgWidth = (rotationDegrees == 90 || rotationDegrees == 270) ? bitmap.Height : bitmap.Width;
        double imgHeight = (rotationDegrees == 90 || rotationDegrees == 270) ? bitmap.Width : bitmap.Height;

        var image = InputImage.FromBitmap(bitmap, rotationDegrees);
        var result = (Text)await _recognizer.Process(image);

        var blocks = new List<OcrTextBlock>();

        if (result != null)
        {
            foreach (var block in result.TextBlocks)
            {
                // ML Kit documents TextBlock.BoundingBox as nullable - it's a real Java Rect
                // binding and does come back null for some blocks regardless of photo quality.
                // Skipping the box (rather than reading .Left/.Top/etc on null) used to throw
                // and fail the whole scan with a generic "something went wrong" error.
                var box = block.BoundingBox;
                blocks.Add(new OcrTextBlock
                {
                    Text = block.Text,
                    BoundingBox = box == null
                        ? Microsoft.Maui.Graphics.Rect.Zero
                        : new Microsoft.Maui.Graphics.Rect(
                            box.Left / imgWidth,
                            box.Top / imgHeight,
                            box.Width() / imgWidth,
                            box.Height() / imgHeight)
                });
            }
        }

        return blocks;
    }
}