using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using WinStatusBar.Services;

namespace WinStatusBar.Views;

/// <summary>
/// Visual do chat compacto. Só forma (raios, tamanhos, espaçamento) e pincéis de tema do WinUI 3 / Fluent:
/// as cores vêm do tema claro/escuro, da cor de destaque do Windows e do Alto Contraste, como em qualquer controle
/// nativo. Os estados (normal, foco, passar o mouse, pressionado, ligado, desabilitado) são os dos modelos padrão
/// do WinUI, sem cores próprias. O material de fundo é o acrílico do sistema (DesktopAcrylicBackdrop), sem véu.
/// Nenhuma regra de negócio.
/// </summary>
internal static class ChatFluentSkin
{
    private static Brush B(string key) => ThemeBrushes.Get(key);

    public static bool IsHighContrast => new AccessibilitySettings().HighContrast;
    public static bool UseAnimations => new UISettings().AnimationsEnabled && !IsHighContrast;

    // Superfícies (pincéis de tema do WinUI 3).
    public static Brush ComposerFill => B("ControlFillColorDefaultBrush");
    public static Brush ComposerStroke => B("ControlStrokeColorDefaultBrush");
    public static Brush CardFill => B("CardBackgroundFillColorDefaultBrush");
    public static Brush CardStroke => B("CardStrokeColorDefaultBrush");
    public static Brush Hairline => B("DividerStrokeColorDefaultBrush");
    public static Brush AccentText => B("AccentTextFillColorPrimaryBrush");

    /// <summary>Botão de destaque nativo (AccentButtonStyle: cor de destaque do Windows), em cápsula.</summary>
    public static void StyleAccentButton(Button button)
    {
        button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Height = 32;
        button.MinHeight = 0;
        button.CornerRadius = new CornerRadius(16);
    }

    /// <summary>Pílula de alternar com os estados nativos do ToggleButton (ligado = cor de destaque do Windows).</summary>
    public static void StylePill(ToggleButton button)
    {
        button.ClearValue(Control.BackgroundProperty);
        button.ClearValue(Control.ForegroundProperty);
        button.ClearValue(Control.BorderBrushProperty);
        button.ClearValue(Control.BorderThicknessProperty);
        button.Height = 32;
        button.MinHeight = 0;
        button.MinWidth = 0;
        button.Padding = new Thickness(11, 0, 12, 0);
        button.CornerRadius = new CornerRadius(16);
    }

    /// <summary>Botão de ícone com o visual padrão do Button do WinUI 3 (preenchimento e contorno de controle).</summary>
    public static void StyleQuietButton(Button button)
    {
        button.ClearValue(Control.BackgroundProperty);
        button.ClearValue(Control.ForegroundProperty);
        button.ClearValue(Control.BorderBrushProperty);
        button.ClearValue(Control.BorderThicknessProperty);
        button.Width = 32;
        button.Height = 32;
        button.CornerRadius = new CornerRadius(16); // redondo, como as pílulas e o enviar
    }

    /// <summary>Enviar: botão de destaque nativo (inclui o estado desabilitado do sistema quando o campo está vazio).</summary>
    public static void StyleSendButton(Button button)
    {
        button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.ClearValue(Control.BackgroundProperty);
        button.ClearValue(Control.ForegroundProperty);
        button.ClearValue(Control.BorderBrushProperty);
        button.Width = 32; // mesma altura dos demais: cantos concêntricos com o campo
        button.Height = 32;
        button.Padding = new Thickness(0);
        button.CornerRadius = new CornerRadius(16);
    }

    /// <summary>
    /// Menus do chat com cantos concêntricos: moldura de raio 12 e itens de raio 7. A distância do item até a borda
    /// é 5 nos dois eixos (contorno 1 + preenchimento da moldura 2 + margem do item 2; dos lados, 1 + 4), e 12 = 7 + 5.
    /// Realce, seleção, foco e acrílico continuam os nativos do WinUI.
    /// </summary>
    public static void RoundMenu(MenuFlyout menu)
    {
        var style = new Style(typeof(MenuFlyoutPresenter)) { BasedOn = menu.MenuFlyoutPresenterStyle };
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(12)));
        menu.MenuFlyoutPresenterStyle = style;
        // Os itens são montados ao abrir (Opening); o arredondamento vem depois, em Opened.
        menu.Opened += (_, _) => RoundItems(menu.Items);
    }

    private static void RoundItems(System.Collections.Generic.IList<MenuFlyoutItemBase> items)
    {
        foreach (var item in items)
        {
            if (item is MenuFlyoutSeparator) continue;
            item.CornerRadius = new CornerRadius(7);
            if (item is MenuFlyoutSubItem sub) RoundItems(sub.Items);
        }
    }

    /// <summary>Microanimação de escala só em botões, ao passar o mouse e clicar. Desligada quando o Windows desliga animações.</summary>
    public static void AttachHoverMotion(FrameworkElement element)
    {
        element.PointerEntered += (_, _) => AnimateScale(element, 1.03f, 140);
        element.PointerExited += (_, _) => AnimateScale(element, 1.0f, 160);
        element.PointerPressed += (_, _) => AnimateScale(element, 0.97f, 80);
        element.PointerReleased += (_, _) => AnimateScale(element, 1.0f, 120);
        element.PointerCaptureLost += (_, _) => AnimateScale(element, 1.0f, 120);
    }

    private static void AnimateScale(FrameworkElement element, float target, int milliseconds)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!UseAnimations || element.ActualWidth < 1 || element.ActualHeight < 1)
        {
            visual.Scale = Vector3.One; // nunca fica preso numa escala se as animações forem desligadas no meio
            return;
        }
        visual.CenterPoint = new Vector3((float)element.ActualWidth / 2f, (float)element.ActualHeight / 2f, 0f);
        var anim = visual.Compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(1f, new Vector3(target, target, 1f));
        anim.Duration = TimeSpan.FromMilliseconds(milliseconds);
        visual.StartAnimation("Scale", anim);
    }
}
