using System.Windows;
using System.Windows.Controls;

namespace FloorballDJ.Controls;

public sealed class InfoLabel : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(InfoLabel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HelpTextProperty = DependencyProperty.Register(
        nameof(HelpText), typeof(string), typeof(InfoLabel), new PropertyMetadata(string.Empty));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string HelpText
    {
        get => (string)GetValue(HelpTextProperty);
        set => SetValue(HelpTextProperty, value);
    }
}
