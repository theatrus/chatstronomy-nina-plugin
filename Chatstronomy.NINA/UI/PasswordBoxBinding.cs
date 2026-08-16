using System.Windows;
using System.Windows.Controls;

namespace Chatstronomy.NINA.UI;

public static class PasswordBoxBinding
{
    // Wires the PasswordChanged handler. This must be a separate property
    // set unconditionally in XAML: the Password callback below only fires
    // when the bound value differs from the dependency-property default
    // (string.Empty), so a fresh profile with empty secrets would never
    // subscribe and typed secrets would never reach the source.
    public static readonly DependencyProperty AttachProperty =
        DependencyProperty.RegisterAttached(
            "Attach",
            typeof(bool),
            typeof(PasswordBoxBinding),
            new PropertyMetadata(false, AttachPropertyChanged));

    public static readonly DependencyProperty PasswordProperty =
        DependencyProperty.RegisterAttached(
            "Password",
            typeof(string),
            typeof(PasswordBoxBinding),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                PasswordPropertyChanged));

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached(
            "IsUpdating",
            typeof(bool),
            typeof(PasswordBoxBinding));

    public static bool GetAttach(DependencyObject element) =>
        (bool)element.GetValue(AttachProperty);

    public static void SetAttach(DependencyObject element, bool value) =>
        element.SetValue(AttachProperty, value);

    public static string GetPassword(DependencyObject element) =>
        (string)element.GetValue(PasswordProperty);

    public static void SetPassword(DependencyObject element, string value) =>
        element.SetValue(PasswordProperty, value);

    private static void AttachPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not PasswordBox passwordBox)
        {
            return;
        }

        if ((bool)args.OldValue)
        {
            passwordBox.PasswordChanged -= PasswordChanged;
        }

        if ((bool)args.NewValue)
        {
            passwordBox.PasswordChanged += PasswordChanged;
        }
    }

    private static void PasswordPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not PasswordBox passwordBox)
        {
            return;
        }

        // Push source -> box, without echoing a box-originated change.
        if (!(bool)passwordBox.GetValue(IsUpdatingProperty))
        {
            passwordBox.Password = args.NewValue as string ?? string.Empty;
        }
    }

    private static void PasswordChanged(object sender, RoutedEventArgs args)
    {
        var passwordBox = (PasswordBox)sender;
        passwordBox.SetValue(IsUpdatingProperty, true);
        passwordBox.SetCurrentValue(PasswordProperty, passwordBox.Password);
        passwordBox.SetValue(IsUpdatingProperty, false);
    }
}
