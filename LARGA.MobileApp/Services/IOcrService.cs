using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Maui.Graphics;

namespace LARGA.MobileApp.Services;

public class OcrTextBlock
{
    public string Text { get; set; }
    public Rect BoundingBox { get; set; }

    /// <summary>The block's text lines with their own boxes, when the OCR engine reports them
    /// (ML Kit does). Receipt parsing pairs labels and amounts per line.</summary>
    public List<OcrTextLine> Lines { get; set; } = new();
}

public class OcrTextLine
{
    public string Text { get; set; } = string.Empty;

    /// <summary>Normalized to 0-1 of the image, like OcrTextBlock.BoundingBox.</summary>
    public Rect BoundingBox { get; set; }

    /// <summary>The line's tilt (dy/dx in normalized units, from its corner points); 0 if unknown.</summary>
    public double Slope { get; set; }
}

public interface IOcrService
{
    // THE FIX: Accepts a file path instead of a massive byte array
    Task<List<OcrTextBlock>> ExtractTextBlocksAsync(string localFilePath);
}
