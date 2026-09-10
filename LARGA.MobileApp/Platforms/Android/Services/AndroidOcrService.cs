using Android.Gms.Extensions;
using Android.Graphics;
using LARGA.MobileApp.Services;
using System.Collections.Generic;
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

        var image = InputImage.FromBitmap(bitmap, 0);

        // Explicitly cast the raw Java object to the ML Kit Text object to expose TextBlocks
        var rawResult = await _recognizer.Process(image);
        var result = (Xamarin.Google.MLKit.Vision.Text.Text)rawResult;

        var blocks = new List<OcrTextBlock>();
        foreach (var block in result.TextBlocks)
        {
            blocks.Add(new OcrTextBlock
            {
                Text = block.Text,
                BoundingBox = new Microsoft.Maui.Graphics.Rect(
                    block.BoundingBox.Left,
                    block.BoundingBox.Top,
                    block.BoundingBox.Width(),
                    block.BoundingBox.Height())
            });
        }
        return blocks;
    }
}