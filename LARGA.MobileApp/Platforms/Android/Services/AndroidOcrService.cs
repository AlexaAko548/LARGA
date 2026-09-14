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
                blocks.Add(new OcrTextBlock
                {
                    Text = block.Text,
                    BoundingBox = new Microsoft.Maui.Graphics.Rect(
                        block.BoundingBox.Left / imgWidth,
                        block.BoundingBox.Top / imgHeight,
                        block.BoundingBox.Width() / imgWidth,
                        block.BoundingBox.Height() / imgHeight)
                });
            }
        }

        return blocks;
    }
}