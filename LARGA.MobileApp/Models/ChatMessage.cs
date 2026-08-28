namespace LARGA.MobileApp.Models;

public class ChatMessage
{
    public string Text { get; set; }
    public bool IsDriver { get; set; } // True = Blue/Right, False = Gray/Left
}