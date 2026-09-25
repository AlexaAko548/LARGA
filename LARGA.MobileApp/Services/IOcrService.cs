using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Maui.Graphics;

namespace LARGA.MobileApp.Services;

public class OcrTextBlock
{
    public string Text { get; set; }
    public Rect BoundingBox { get; set; }
}

public interface IOcrService
{
    // THE FIX: Accepts a file path instead of a massive byte array
    Task<List<OcrTextBlock>> ExtractTextBlocksAsync(string localFilePath);
}
