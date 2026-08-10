using System.Windows;

namespace FloorballDJ.Views;

public partial class TextPromptWindow : Window
{
    public string Value => ValueBox.Text.Trim();

    public TextPromptWindow(string title, string heading, string description, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = heading;
        DescriptionText.Text = description;
        ValueBox.Text = initialValue;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueBox.Text))
        {
            MessageBox.Show(this, "Namnet kan inte vara tomt.", "Ange ett namn", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
