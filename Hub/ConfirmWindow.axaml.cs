using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EmpireHub;

// A question with two answers: shows the message and closes with true when the confirm button is pressed, otherwise false.
public partial class ConfirmWindow : Window
{
    public ConfirmWindow() => InitializeComponent();

    public ConfirmWindow(string title, string message, string confirmText) : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
