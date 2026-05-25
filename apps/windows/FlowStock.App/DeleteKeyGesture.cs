using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FlowStock.App;

internal static class DeleteKeyGesture
{
    public static bool IsDeleteGesture(KeyEventArgs e)
    {
        return e.Key == Key.Delete
               && Keyboard.Modifiers == ModifierKeys.None
               && !IsTextInputOrigin(e.OriginalSource as DependencyObject);
    }

    private static bool IsTextInputOrigin(DependencyObject? source)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox)
            {
                return true;
            }

            if (current is System.Windows.Controls.ComboBox comboBox && comboBox.IsEditable)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is FrameworkContentElement contentElement)
        {
            return contentElement.Parent;
        }

        return VisualTreeHelper.GetParent(current);
    }
}
