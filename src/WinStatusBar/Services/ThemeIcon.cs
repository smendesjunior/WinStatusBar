using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinStatusBar.Services;

/// <summary>Resolve ícones monocromáticos pelo tema do controle, independentemente do tema global e dos outros monitores.</summary>
public static class ThemeIcon
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(ImageSource), typeof(ThemeIcon), new PropertyMetadata(null, OnSourceChanged));

    public static ImageSource? GetSource(DependencyObject obj) => (ImageSource?)obj.GetValue(SourceProperty);
    public static void SetSource(DependencyObject obj, ImageSource? value) => obj.SetValue(SourceProperty, value);

    private static void OnSourceChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is not FrameworkElement element) return;
        element.ActualThemeChanged -= OnThemeChanged;
        element.ActualThemeChanged += OnThemeChanged;
        element.Loaded -= OnLoaded;
        element.Loaded += OnLoaded;
        Apply(element);
    }

    private static void OnThemeChanged(FrameworkElement element, object args) => Apply(element);
    private static void OnLoaded(object sender, RoutedEventArgs args) => Apply((FrameworkElement)sender);

    /// <summary>Logotipo monocromático pelo nome (ver IconService.MonoLogoAsync), refeito quando o tema do controle muda.</summary>
    public static readonly DependencyProperty MonoLogoProperty = DependencyProperty.RegisterAttached(
        "MonoLogo", typeof(string), typeof(ThemeIcon), new PropertyMetadata(null, OnMonoLogoChanged));

    public static string? GetMonoLogo(DependencyObject obj) => (string?)obj.GetValue(MonoLogoProperty);
    public static void SetMonoLogo(DependencyObject obj, string? value) => obj.SetValue(MonoLogoProperty, value);

    private static void OnMonoLogoChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is not FrameworkElement element) return;
        element.ActualThemeChanged -= OnMonoThemeChanged;
        element.ActualThemeChanged += OnMonoThemeChanged;
        element.Loaded -= OnMonoLoaded;
        element.Loaded += OnMonoLoaded;
        _ = ApplyMonoAsync(element);
    }

    private static void OnMonoThemeChanged(FrameworkElement element, object args) => _ = ApplyMonoAsync(element);
    private static void OnMonoLoaded(object sender, RoutedEventArgs args) => _ = ApplyMonoAsync((FrameworkElement)sender);

    private static async Task ApplyMonoAsync(FrameworkElement element)
    {
        if (GetMonoLogo(element) is not { Length: > 0 } name) return;
        bool dark = element.ActualTheme == ElementTheme.Dark;
        var source = await IconService.MonoLogoAsync(name, dark);
        if (source is null || GetMonoLogo(element) != name || (element.ActualTheme == ElementTheme.Dark) != dark) return;
        if (element is Image image) image.Source = source;
        else if (element is ImageIcon icon) icon.Source = source;
    }

    private static void Apply(FrameworkElement element)
    {
        var source = GetSource(element);
        if (source is not null && (ReferenceEquals(source, IconService.CodexIcon(true))
            || ReferenceEquals(source, IconService.CodexIcon(false))))
            source = IconService.CodexIcon(element.ActualTheme == ElementTheme.Dark) ?? source;
        if (element is Image image) image.Source = source;
        else if (element is ImageIcon icon) icon.Source = source;
    }
}
