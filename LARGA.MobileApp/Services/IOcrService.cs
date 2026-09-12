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
    // Accepts continuous camera frames for real-time viewfinder text extraction
    Task<List<OcrTextBlock>> ExtractTextBlocksAsync(byte[] imageBytes);
}