using System.Runtime.InteropServices;

namespace DiscordVpnLauncher;

internal enum FailureChoice
{
    /// <summary>Encerrar tudo sem abrir o Discord.</summary>
    Fechar,

    /// <summary>Abrir o Discord no IP real brasileiro.</summary>
    ContinuarSemVpn,
}

/// <summary>
/// P/Invoke minimo. Usa MessageBox de user32.dll em vez de System.Windows.Forms
/// para nao arrastar o WinForms (e o custo de tamanho dele) para um console app.
/// </summary>
internal static class NativeMethods
{
    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_DEFBUTTON2 = 0x00000100; // default = Nao (Fechar): escolha conservadora
    private const uint MB_SETFOREGROUND = 0x00010000;
    private const uint MB_TOPMOST = 0x00040000;

    private const int IDYES = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>
    /// Popup de falha com as duas saidas do plano.
    ///
    /// MessageBox nao permite renomear os botoes (isso exigiria TaskDialogIndirect,
    /// que depende de manifest do comctl32 v6 e falha silenciosamente sem ele), por
    /// isso o mapeamento Sim/Nao vai explicito no texto.
    /// </summary>
    public static FailureChoice ShowFailurePopup(string detail)
    {
        var text =
            "Erro ao conectar em outro pais.\n\n" +
            $"Motivo: {detail}\n\n" +
            "Sim  = Continuar sem VPN (abre o Discord no seu IP real brasileiro)\n" +
            "Nao  = Fechar (nao abre o Discord)";

        var result = MessageBoxW(
            IntPtr.Zero,
            text,
            "Discord VPN Launcher",
            MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2 | MB_SETFOREGROUND | MB_TOPMOST);

        return result == IDYES ? FailureChoice.ContinuarSemVpn : FailureChoice.Fechar;
    }
}
