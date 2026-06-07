using System.Windows;
using System.Windows.Input;

namespace Aml.Editor.Plugin.FPB.Views;

/// <summary>
/// Minimal modal text-input dialog. WPF has no built-in equivalent of
/// VB's InputBox; this is a tiny stand-in used for the "New Process" flow.
/// </summary>
public partial class InputDialog : Window
{
    public string Result { get; private set; } = string.Empty;

    public InputDialog(string title, string prompt, string defaultValue)
    {
        InitializeComponent();
        Title = title;
        PromptLabel.Text = prompt;
        InputBox.Text = defaultValue ?? string.Empty;
        Loaded += (_, __) =>
        {
            InputBox.SelectAll();
            InputBox.Focus();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = InputBox.Text ?? string.Empty;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
    }
}
