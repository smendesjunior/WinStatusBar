using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinStatusBar.Services;

/// <summary>Material de sistema para menus temporários, inclusive fora da AppBar.</summary>
internal static class PopupMaterial
{
    /// <summary>
    /// Menus e painéis seguem o tema do Windows (moldura, acrílico e texto), não o contraste da barra com o papel de
    /// parede: sobe do conteúdo até o FlyoutPresenter e aplica o tema do app ao abrir.
    /// </summary>
    public static void FollowAppTheme(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase flyout, FrameworkElement? content = null)
    {
        flyout.Opened += (_, _) =>
        {
            var theme = WinStatusBar.App.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
            DependencyObject? node = content ?? (flyout as Flyout)?.Content as FrameworkElement;
            while (node is not null && node is not FlyoutPresenter) node = VisualTreeHelper.GetParent(node);
            if (node is FlyoutPresenter presenter)
            {
                if (presenter.RequestedTheme != theme) presenter.RequestedTheme = theme;
                // O acrílico de sistema do menu não obedece ao tema do elemento: sobre papel de parede claro ficava
                // claro com texto branco. Uma camada de 85 % na cor do tema garante o contraste e mantém leve translucidez.
                presenter.Background = new SolidColorBrush(theme == ElementTheme.Dark
                    ? Windows.UI.Color.FromArgb(0xD9, 0x20, 0x20, 0x20) : Windows.UI.Color.FromArgb(0xD9, 0xF3, 0xF3, 0xF3));
            }
        };
    }

    public static void Apply(Flyout flyout)
    {
        flyout.ShouldConstrainToRootBounds = false;
        flyout.SystemBackdrop = new DesktopAcrylicBackdrop();
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(FlyoutPresenter.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
        style.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(8)));
        flyout.FlyoutPresenterStyle = style;
        FollowAppTheme(flyout);
    }
    public static void Apply(MenuFlyout menu)
    {
        menu.ShouldConstrainToRootBounds = false;
        menu.SystemBackdrop = new DesktopAcrylicBackdrop();
        var style = new Style(typeof(MenuFlyoutPresenter));
        style.Setters.Add(new Setter(MenuFlyoutPresenter.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
        style.Setters.Add(new Setter(MenuFlyoutPresenter.CornerRadiusProperty, new CornerRadius(8)));
        menu.MenuFlyoutPresenterStyle = style;
    }
}
