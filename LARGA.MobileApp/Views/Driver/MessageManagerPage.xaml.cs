using LARGA.MobileApp.ViewModels.Driver;
using Microsoft.Maui.Controls;
using System;

namespace LARGA.MobileApp.Views.Driver;

public partial class MessageManagerPage : ContentPage
{
    public MessageManagerPage(ViewModels.Driver.MessageManagerViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnBackButtonClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }

    public class ChatMessage
    {
        public string Text { get; set; }
        public bool IsDriver { get; set; } // True = Blue/Right, False = Gray/Left
    }
}