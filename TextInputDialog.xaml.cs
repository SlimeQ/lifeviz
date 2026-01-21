using System.Windows;

namespace lifeviz;

public partial class TextInputDialog : Window
{
    public TextInputDialog(string title, string message, string defaultText = "")
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        InputTextBox.Text = defaultText;
        InputTextBox.SelectAll();
        InputTextBox.Focus();
    }

    public string InputText { get; private set; } = string.Empty;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        InputText = InputTextBox.Text;
        DialogResult = true;
    }
}
