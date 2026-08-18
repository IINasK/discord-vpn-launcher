using System.Runtime.InteropServices;

namespace DiscordVpnLauncher;

internal enum FailureChoice
{
    /// <summary>Encerrar tudo sem abrir o Discord.</summary>
    Fechar,

    /// <summary>Abrir o Discord no IP real brasileiro.</summary>
    ContinuarSemVpn,
}

internal enum LocateChoice
{
    /// <summary>Desistir; o launcher encerra sem abrir nada.</summary>
    Desistir,

    /// <summary>Abrir o seletor de arquivo para apontar o Discord.</summary>
    Localizar,
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
    /// Perguntado quando a deteccao automatica do Discord falha - antes de abrir o
    /// seletor de arquivo, para nao jogar um dialogo na cara de quem so quer sair.
    /// </summary>
    public static LocateChoice ShowLocateDiscordPrompt()
    {
        var text =
            "Não foi possível localizar a instalação do Discord neste computador.\n\n" +
            "Isso ocorre quando ele está instalado em outro disco ou em uma pasta " +
            "fora do padrão.\n\n" +
            "Sim  =  Localizar o Discord (você indica o arquivo; a escolha será lembrada)\n" +
            "Não  =  Fechar";

        var result = MessageBoxW(
            IntPtr.Zero,
            text,
            "Discord VPN Launcher",
            MB_YESNO | MB_ICONWARNING | MB_SETFOREGROUND | MB_TOPMOST);

        return result == IDYES ? LocateChoice.Localizar : LocateChoice.Desistir;
    }

    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_EXPLORER = 0x00080000;
    private const int OFN_NOCHANGEDIR = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    /// <summary>
    /// Dialogo "abrir arquivo" do proprio Windows, por P/Invoke.
    ///
    /// Ultimo recurso para achar o Discord: so aparece quando a deteccao
    /// automatica falhou. Vale a chamada nativa para nao arrastar o WinForms
    /// (e o tamanho dele) so por causa de um seletor de arquivo.
    /// </summary>
    /// <returns>Caminho escolhido, ou null se o usuario cancelou.</returns>
    public static string? EscolherExecutavel(string titulo, string pastaInicial)
    {
        // O filtro usa \0 como separador e termina em \0\0 - convencao da API.
        var filtro = "Executaveis (*.exe)\0*.exe\0Todos os arquivos\0*.*\0\0";
        var buffer = Marshal.AllocHGlobal(2 * 260 * 2); // WCHAR, com folga

        try
        {
            Marshal.Copy(new byte[2 * 260 * 2], 0, buffer, 2 * 260 * 2);

            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                lpstrFilter = filtro,
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = 260 * 2,
                lpstrTitle = titulo,
                lpstrInitialDir = pastaInicial,
                // NOCHANGEDIR: o dialogo nao pode mudar o diretorio de trabalho do
                // processo - o openvpn.exe depende dele para achar as DLLs.
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER | OFN_NOCHANGEDIR,
            };

            if (!GetOpenFileNameW(ref ofn))
                return null; // cancelado (ou erro; nos dois casos nao ha escolha)

            var caminho = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(caminho) ? null : caminho;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

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
            "Não foi possível conectar a um servidor fora do Brasil.\n\n" +
            $"Motivo: {detail}\n\n" +
            "Sim  =  Continuar sem VPN (o Discord será aberto com o seu IP real brasileiro)\n" +
            "Não  =  Fechar (o Discord não será aberto)";

        var result = MessageBoxW(
            IntPtr.Zero,
            text,
            "Discord VPN Launcher",
            MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2 | MB_SETFOREGROUND | MB_TOPMOST);

        return result == IDYES ? FailureChoice.ContinuarSemVpn : FailureChoice.Fechar;
    }
}
