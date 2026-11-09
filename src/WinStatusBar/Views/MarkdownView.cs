using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinStatusBar.Views.Panels;
using L = WinStatusBar.Services.Localization;

namespace WinStatusBar.Views;

/// <summary>
/// Markdown das respostas da IA em controles nativos do WinUI 3, no visual dos apps de chat:
/// títulos, parágrafos com **negrito**, *itálico*, `código` e links clicáveis, listas (com níveis),
/// citações, blocos de código com botão de copiar, tabelas, linhas divisórias e imagens.
/// </summary>
internal static class MarkdownView
{
    internal const double BodyFontSize = 14;
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public static void Render(StackPanel host, string markdown)
    {
        host.Children.Clear();
        var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        int i = 0;
        var para = new List<string>();

        void FlushParagraph()
        {
            if (para.Count == 0) return;
            string text = string.Join(" ", para.Select(p => p.Trim()));
            host.Children.Add(IsNote(text) ? Note(text) : Paragraph(text));
            para.Clear();
        }

        while (i < lines.Length)
        {
            string line = lines[i];
            string t = line.Trim();

            // Bloco de código ```
            if (t.StartsWith("```"))
            {
                FlushParagraph();
                string lang = t[3..].Trim();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith("```")) code.Add(lines[i++]);
                i++; // fecha ```
                host.Children.Add(CodeBlock(string.Join("\n", code), lang));
                continue;
            }
            if (t.Length == 0) { FlushParagraph(); i++; continue; }

            // Título
            var h = Regex.Match(t, @"^(#{1,6})\s+(.*)$");
            if (h.Success) { FlushParagraph(); host.Children.Add(Heading(h.Groups[2].Value, h.Groups[1].Value.Length)); i++; continue; }

            // Linha divisória
            if (Regex.IsMatch(t, @"^(-{3,}|\*{3,}|_{3,})$")) { FlushParagraph(); host.Children.Add(Divider()); i++; continue; }

            // Imagem sozinha na linha
            var img = Regex.Match(t, @"^!\[([^\]]*)\]\((\S+?)(?:\s+""[^""]*"")?\)$");
            if (img.Success) { FlushParagraph(); host.Children.Add(ImageBlock(img.Groups[2].Value, img.Groups[1].Value)); i++; continue; }

            // Tabela | a | b |
            if (t.StartsWith('|') && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1].Trim(), @"^\|?\s*:?-{2,}"))
            {
                FlushParagraph();
                var rows = new List<string> { t };
                i += 2;
                while (i < lines.Length && lines[i].Trim().StartsWith('|')) rows.Add(lines[i++].Trim());
                host.Children.Add(Table(rows));
                continue;
            }

            // Citação
            if (t.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new List<string>();
                while (i < lines.Length && lines[i].Trim().StartsWith('>')) quote.Add(lines[i++].Trim().TrimStart('>').Trim());
                host.Children.Add(Quote(string.Join(" ", quote)));
                continue;
            }

            // Lista (com níveis por indentação)
            var li = Regex.Match(line, @"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
            if (li.Success)
            {
                FlushParagraph();
                var list = new StackPanel { Spacing = 4 };
                while (i < lines.Length)
                {
                    var m = Regex.Match(lines[i], @"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
                    if (!m.Success)
                    {
                        // continuação da linha do item (texto recuado)
                        if (lines[i].StartsWith("  ") && lines[i].Trim().Length > 0 && list.Children.Count > 0)
                        {
                            list.Children.Add(ListItem(string.Empty, lines[i].Trim(), lines[i].Length - lines[i].TrimStart().Length));
                            i++; continue;
                        }
                        break;
                    }
                    string marker = m.Groups[2].Value;
                    list.Children.Add(ListItem(char.IsDigit(marker[0]) ? marker.TrimEnd(')').TrimEnd('.') + "." : "•", m.Groups[3].Value, m.Groups[1].Value.Replace("\t", "    ").Length));
                    i++;
                }
                host.Children.Add(list);
                continue;
            }

            para.Add(line);
            i++;
        }
        FlushParagraph();
    }

    // ------------------------------------------------------------------ blocos
    private static FrameworkElement Paragraph(string text)
    {
        var rtb = NewRich(BodyFontSize);
        var p = new Paragraph();
        AddInlines(p.Inlines, text);
        rtb.Blocks.Add(p);
        return rtb;
    }

    /// <summary>"Nota:", "Obs.:", "Observação:" ou um parágrafo inteiro em itálico: rodapé discreto, como nos apps de chat.</summary>
    private static bool IsNote(string text)
        => Regex.IsMatch(text, @"^(\*\*|__)?(Nota|Obs\.?|Observação|Observacao|Note)(\*\*|__)?\s*:", RegexOptions.IgnoreCase)
           || Regex.IsMatch(text, @"^(\*|_)[^*_].*[^*_](\*|_)$");

    private static FrameworkElement Note(string text)
    {
        var rtb = NewRich(BodyFontSize);
        rtb.LineHeight = BodyFontSize + 8;
        rtb.Foreground = PanelKit.Res("TextFillColorSecondaryBrush");
        rtb.Margin = new Thickness(0, 2, 0, 0);
        var p = new Paragraph();
        AddInlines(p.Inlines, Regex.Replace(text, @"^(\*|_)(.*)(\*|_)$", "$2"));
        rtb.Blocks.Add(p);
        return rtb;
    }

    private static FrameworkElement Heading(string text, int level)
    {
        // Hierarquia clara: título da resposta grande e em negrito; subtítulos menores.
        double size = level switch { 1 => 22, 2 => 19, 3 => 16, _ => 15 };
        var rtb = NewRich(size);
        rtb.LineHeight = size + 6;
        rtb.FontWeight = level <= 2 ? FontWeights.Bold : FontWeights.SemiBold;
        rtb.Margin = new Thickness(0, level <= 2 ? 6 : 4, 0, 0);
        var p = new Paragraph();
        AddInlines(p.Inlines, text);
        rtb.Blocks.Add(p);
        return rtb;
    }

    private static FrameworkElement ListItem(string marker, string text, int indent)
    {
        var g = new Grid { ColumnSpacing = 8, Margin = new Thickness(Math.Min(indent, 12) * 8 + 2, 0, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(marker.Length > 1 ? 22 : 12) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        bool numbered = marker.Length > 0 && char.IsDigit(marker[0]);
        g.Children.Add(new TextBlock { Text = marker, FontSize = BodyFontSize, LineHeight = BodyFontSize + 8,
            FontWeight = numbered ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = PanelKit.Res(numbered ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush") });
        var rtb = NewRich(BodyFontSize);
        var p = new Paragraph();
        AddInlines(p.Inlines, text);
        rtb.Blocks.Add(p);
        Grid.SetColumn(rtb, 1);
        g.Children.Add(rtb);
        return g;
    }

    private static FrameworkElement Quote(string text)
    {
        var rtb = NewRich(BodyFontSize);
        rtb.Foreground = PanelKit.Res("TextFillColorSecondaryBrush");
        var p = new Paragraph();
        AddInlines(p.Inlines, text);
        rtb.Blocks.Add(p);
        return new Border
        {
            BorderBrush = PanelKit.Res("AccentFillColorDefaultBrush"), BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 2, 0, 2), Child = rtb,
        };
    }

    private static FrameworkElement Divider()
        => new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8), Background = PanelKit.Res("DividerStrokeColorDefaultBrush") };

    private static FrameworkElement CodeBlock(string code, string lang)
    {
        var header = new Grid { Padding = new Thickness(12, 4, 4, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = lang.Length > 0 ? lang : L.T("código"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = PanelKit.Res("TextFillColorSecondaryBrush") });
        var copy = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 11 }, Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(copy, L.T("Copiar código"));
        copy.Click += (_, _) => CopyText(code);
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);

        var body = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled,
            Padding = new Thickness(12, 0, 12, 10),
            Content = new TextBlock { Text = code, FontFamily = Mono, FontSize = BodyFontSize, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap },
        };
        var stack = new StackPanel();
        stack.Children.Add(header); stack.Children.Add(body);
        return new Border
        {
            Background = PanelKit.Res("CardBackgroundFillColorSecondaryBrush"), BorderBrush = PanelKit.Res("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = stack, Margin = new Thickness(0, 2, 0, 2),
        };
    }

    private static FrameworkElement Table(List<string> rows)
    {
        static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var cells = rows.Select(Cells).ToList();
        int cols = cells.Max(c => c.Length);
        var grid = new Grid();
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 60 });
        for (int r = 0; r < cells.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < cols; c++)
            {
                var rtb = NewRich(BodyFontSize);
                if (r == 0) rtb.FontWeight = FontWeights.SemiBold;
                var p = new Paragraph();
                AddInlines(p.Inlines, c < cells[r].Length ? cells[r][c] : string.Empty);
                rtb.Blocks.Add(p);
                var cell = new Border
                {
                    Padding = new Thickness(10, 6, 10, 6), Child = rtb, MaxWidth = 280,
                    BorderBrush = PanelKit.Res("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(0, 0, 0, 1),
                    Background = r == 0 ? PanelKit.Res("SubtleFillColorSecondaryBrush") : null,
                };
                Grid.SetRow(cell, r); Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return new Border
        {
            CornerRadius = new CornerRadius(8), BorderBrush = PanelKit.Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
            Child = new ScrollViewer
            {
                Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled,
            },
        };
    }

    private static FrameworkElement ImageBlock(string url, string alt)
    {
        var image = new Image { Stretch = Stretch.Uniform, MaxHeight = 260, HorizontalAlignment = HorizontalAlignment.Left };
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https" or "file"))
                image.Source = new BitmapImage(uri);
        }
        catch { }
        var frame = new Border { CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left, Child = image, Margin = new Thickness(0, 4, 0, 4) };
        // Clique amplia; botão direito copia, salva, abre no visualizador ou mostra na pasta (todas as IAs).
        if (Uri.TryCreate(url, UriKind.Absolute, out var target) && target.Scheme is "http" or "https" or "file") ChatImageActions.Attach(frame, target, alt);
        else if (alt.Length > 0) ToolTipService.SetToolTip(image, alt);
        return frame;
    }

    // ------------------------------------------------------------------ texto em linha
    private static RichTextBlock NewRich(double size) => new()
    {
        FontSize = size, LineHeight = size + 8, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
    };

    /// <summary>**negrito**, __negrito__, *itálico*, _itálico_, ~~riscado~~, `código`, [texto](url), &lt;url&gt; e URLs soltas.</summary>
    private static readonly Regex InlineRx = new(
        @"(\*\*(?<b>.+?)\*\*)|(__(?<b2>.+?)__)|(~~(?<s>.+?)~~)|(`(?<c>[^`]+)`)|(!\[(?<ia>[^\]]*)\]\((?<iu>[^)\s]+)\))|(\[(?<lt>[^\]]+)\]\((?<lu>[^)\s]+)(?:\s+""[^""]*"")?\))|(<(?<au>https?://[^>\s]+)>)|(?<u>https?://[^\s)<>\]]+)|((?<![\w*])\*(?<i>[^*\s][^*]*?)\*(?![\w*]))|((?<![\w_])_(?<i2>[^_\s][^_]*?)_(?![\w_]))",
        RegexOptions.Compiled);

    private static void AddInlines(InlineCollection target, string text)
    {
        int pos = 0;
        foreach (Match m in InlineRx.Matches(text))
        {
            if (m.Index > pos) target.Add(new Run { Text = text[pos..m.Index] });
            if (m.Groups["b"].Success || m.Groups["b2"].Success)
            {
                var bold = new Bold();
                AddInlines(bold.Inlines, m.Groups["b"].Success ? m.Groups["b"].Value : m.Groups["b2"].Value);
                target.Add(bold);
            }
            else if (m.Groups["i"].Success || m.Groups["i2"].Success)
            {
                var it = new Italic();
                AddInlines(it.Inlines, m.Groups["i"].Success ? m.Groups["i"].Value : m.Groups["i2"].Value);
                target.Add(it);
            }
            else if (m.Groups["s"].Success) target.Add(new Run { Text = m.Groups["s"].Value, TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough });
            else if (m.Groups["c"].Success)
            {
                // `código` em linha numa pílula com fundo próprio (como "net use" na referência).
                var code = new Border
                {
                    Background = PanelKit.Res("ControlFillColorSecondaryBrush"), BorderBrush = PanelKit.Res("ControlStrokeColorDefaultBrush"),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1),
                    Margin = new Thickness(1, 0, 1, -4),
                    Child = new TextBlock { Text = m.Groups["c"].Value, FontFamily = Mono, FontSize = BodyFontSize, IsTextSelectionEnabled = true },
                };
                target.Add(new InlineUIContainer { Child = code });
            }
            else if (m.Groups["iu"].Success) target.Add(Link(m.Groups["ia"].Value.Length > 0 ? "🖼 " + m.Groups["ia"].Value : "🖼", m.Groups["iu"].Value));
            else if (m.Groups["lt"].Success) target.Add(Link(m.Groups["lt"].Value, m.Groups["lu"].Value));
            else if (m.Groups["au"].Success) target.Add(Link(m.Groups["au"].Value, m.Groups["au"].Value));
            else if (m.Groups["u"].Success) target.Add(Link(ShortUrl(m.Groups["u"].Value), m.Groups["u"].Value));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) target.Add(new Run { Text = text[pos..] });
    }

    private static Inline Link(string label, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto"))
            return new Run { Text = label };
        var link = new Hyperlink { NavigateUri = uri, UnderlineStyle = UnderlineStyle.None };
        link.Inlines.Add(new Run { Text = label });
        ToolTipService.SetToolTip(link, url);
        return link;
    }

    private static string ShortUrl(string url)
    {
        try { var u = new Uri(url); string s = u.Host.StartsWith("www.") ? u.Host[4..] : u.Host; return u.AbsolutePath.Length > 1 ? s + "/…" : s; }
        catch { return url; }
    }

    private static void CopyText(string text)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }
}
