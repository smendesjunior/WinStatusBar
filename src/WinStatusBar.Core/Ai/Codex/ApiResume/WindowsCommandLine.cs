using System.Text;

namespace WinStatusBar.Core.Ai.Codex.ApiResume;

/// <summary>Monta uma linha de comando que o Windows (CommandLineToArgvW / CRT) desmonta de volta nos mesmos argumentos.
/// Acentos do OneDrive, espaços, aspas e "Z:\" passam intactos.</summary>
public static class WindowsCommandLine
{
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            // Barras antes de aspas são dobradas e a aspa é escapada; barras soltas ficam como estão.
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            sb.Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2); // barras no fim: dobradas, para não escaparem a aspa de fechamento
        return sb.Append('"').ToString();
    }

    public static string Join(string exe, IEnumerable<string> args) =>
        string.Join(' ', new[] { Quote(exe) }.Concat(args.Select(Quote)));
}
