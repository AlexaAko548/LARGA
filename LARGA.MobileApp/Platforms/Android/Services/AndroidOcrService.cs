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
                var ocrBlock = new OcrTextBlock
                {
                    Text = block.Text,
                    BoundingBox = Normalize(box, imgWidth, imgHeight)
                };

                foreach (var line in block.Lines)
                {
                    ocrBlock.Lines.Add(new OcrTextLine
                    {
                        Text = line.Text,
                        BoundingBox = Normalize(line.BoundingBox, imgWidth, imgHeight),
                        Slope = Slope(line.GetCornerPoints(), imgWidth, imgHeight)
                    });
                }

                blocks.Add(ocrBlock);
#if DEBUG
                System.Diagnostics.Debug.WriteLine(
                    $"LARGA-OCR block {blocks.Count - 1} img={imgWidth}x{imgHeight} " +
                    $"px=[L{box?.Left} T{box?.Top} R{box?.Right} B{box?.Bottom}] " +
                    $"norm=[x{ocrBlock.BoundingBox.X:0.####} y{ocrBlock.BoundingBox.Y:0.####} " +
                    $"w{ocrBlock.BoundingBox.Width:0.####} h{ocrBlock.BoundingBox.Height:0.####}] " +
                    $"text=\"{block.Text?.Replace("\n", "\\n")}\"");
                foreach (var l in ocrBlock.Lines)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"LARGA-OCR line {blocks.Count - 1} " +
                        $"norm=[x{l.BoundingBox.X:0.####} y{l.BoundingBox.Y:0.####} " +
                        $"w{l.BoundingBox.Width:0.####} h{l.BoundingBox.Height:0.####}] slope={l.Slope:0.#####} " +
                        $"text=\"{l.Text}\"");
                }
#endif
            }
        }

        return blocks;
    }

    private static Microsoft.Maui.Graphics.Rect Normalize(global::Android.Graphics.Rect? box, double imgWidth, double imgHeight) =>
        box == null || imgWidth <= 0 || imgHeight <= 0
            ? Microsoft.Maui.Graphics.Rect.Zero
            : new Microsoft.Maui.Graphics.Rect(
                box.Left / imgWidth,
                box.Top / imgHeight,
                box.Width() / imgWidth,
                box.Height() / imgHeight);

    /// <summary>Tilt of a text line from its top edge (corner points: top-left, top-right, ...),
    /// as dy/dx in normalized image units.</summary>
    private static double Slope(global::Android.Graphics.Point[]? corners, double imgWidth, double imgHeight)
    {
        if (corners == null || corners.Length < 2 || imgWidth <= 0 || imgHeight <= 0)
            return 0;

        double dx = (corners[1].X - corners[0].X) / imgWidth;
        double dy = (corners[1].Y - corners[0].Y) / imgHeight;
        return dx > 0 ? dy / dx : 0;
    }
}