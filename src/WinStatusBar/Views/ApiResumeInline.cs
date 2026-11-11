using Microsoft.UI.Xaml;

namespace WinStatusBar.Views;

/// <summary>
/// Escolha e confirmações do "Continuar pela API" acontecem dentro do próprio menu (a barra tem só a altura da faixa,
/// então uma janela de diálogo dela ficaria invisível). Enquanto uma delas está aberta, o menu da cota e o cartão do
/// Acompanhamento não se refazem por baixo; a pausa acaba ao confirmar, cancelar ou fechar o menu.
/// </summary>
internal static class ApiResumeInline
{
    private static int _held;

    public static bool Busy => Volatile.Read(ref _held) > 0;

    /// <summary>Pausa as atualizações; devolve quem solta a pausa (uma vez só; também solta quando o elemento sai da tela).</summary>
    public static Action Hold(FrameworkElement owner)
    {
        Interlocked.Increment(ref _held);
        int released = 0;
        void Release()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) Interlocked.Decrement(ref _held);
        }
        owner.Unloaded += (_, _) => Release();
        return Release;
    }
}
