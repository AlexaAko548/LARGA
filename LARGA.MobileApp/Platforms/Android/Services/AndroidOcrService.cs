using Android.Gms.Extensions;
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

    public async Task<List<OcrTextBlock>> ExtractTextBlocksAsync(string localFilePath)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
            return new List<OcrTextBlock>();

        var context = global::Android.App.Application.Context;
        var javaFile = new global::Java.IO.File(localFilePath);
        var androidUri = global::Android.Net.Uri.FromFile(javaFile);

        // THE FIX: The file path now safely points to a lightweight, 1024px upright JPEG
        using var image = InputImage.FromFilePath(context, androidUri);
        var result = (Text)await _recognizer.Process(image);

        var blocks = new List<OcrTextBlock>();

        if (result != null)
        {
            double imgWidth = image.Width;
            double imgHeight = image.Height;

            foreach (var block in result.TextBlocks)
            {
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