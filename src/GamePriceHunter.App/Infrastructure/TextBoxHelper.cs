using System.Windows;

namespace GamePriceHunter.App.Infrastructure;

/// <summary>Attached properties used by the TextBox template.</summary>
public static class TextBoxHelper
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(TextBoxHelper), new FrameworkPropertyMetadata(string.Empty));

    public static string GetPlaceholder(DependencyObject element) => (string)element.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject element, string value) => element.SetValue(PlaceholderProperty, value);
}
