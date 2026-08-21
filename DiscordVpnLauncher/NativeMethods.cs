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

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONINFORMATION = 0x00000040;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_CLOSE = 0x0010;
    private const int IDOK = 1;

    /// <summary>Classe de janela de todo dialogo padrao do Windows, MessageBox incluso.</summary>
    private const string ClasseDialogo = "#32770";

    /// <summary>
    /// Titulo proprio, diferente do usado nos outros popups e do Console.Title: e por
    /// ele que o fechamento automatico encontra a janela, e acertar a janela errada
    /// significaria deixar o dialogo de pe (e o tunel junto).
    /// </summary>
    private const string TituloDesligar = "Discord VPN Launcher - VPN ligada";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Segura o teardown ate o usuario mandar desligar a VPN.
    ///
    /// O botao e manual porque so o usuario sabe se o Discord ja terminou de carregar
    /// na tela dele. Mas o clique NAO pode ser a unica saida: o invariante e que o
    /// tunel nunca fica aberto, e um popup ignorado (usuario saiu, tela bloqueada)
    /// deixaria o trafego inteiro passando pelo relay indefinidamente. Por isso o
    /// dialogo vive em uma thread propria e, estourado o teto, e dispensado daqui de
    /// fora com um WM_COMMAND/IDOK - o mesmo que o clique produziria.
    /// </summary>
    /// <returns>true se o usuario clicou; false se o teto de tempo dispensou o popup.</returns>
    public static bool ShowTeardownPrompt(TimeSpan teto)
    {
        var text =
            "O Discord já registrou o IP da VPN.\n\n" +
            "A VPN continua ligada até você clicar em OK — enquanto isso, todo o " +
            "tráfego passa pelo servidor no exterior e o ping em call fica alto.\n\n" +
            "Clique em OK assim que o Discord terminar de carregar.\n\n" +
            $"Sem resposta, ela será desligada sozinha em {teto.TotalMinutes:0} minuto(s).";

        var clicou = false;

        var thread = new Thread(() =>
        {
            MessageBoxW(
                IntPtr.Zero,
                text,
                TituloDesligar,
                MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST);

            clicou = true;
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (thread.Join(teto))
            return clicou;

        // Estourou: dispensa o dialogo e segue para o teardown. As mensagens vao por
        // PostMessage porque quem processa a fila e a thread dona da janela, e vao as
        // duas porque uma pode nao pegar - o WM_COMMAND/IDOK e o equivalente exato ao
        // clique e o WM_CLOSE e a rede de seguranca. Insiste por alguns segundos: a
        // janela pode nao estar respondendo no instante exato do estouro.
        var prazo = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (DateTime.UtcNow < prazo)
        {
            var janela = FindWindowW(ClasseDialogo, TituloDesligar);

            if (janela != IntPtr.Zero)
            {
                PostMessageW(janela, WM_COMMAND, (IntPtr)IDOK, IntPtr.Zero);
                PostMessageW(janela, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }

            if (thread.Join(TimeSpan.FromMilliseconds(500)))
                break;
        }

        // Se o popup insistir em ficar na tela, o teardown acontece do mesmo jeito:
        // a thread e de background e nao segura a saida do processo. A VPN cair nunca
        // pode depender de a janela ter fechado.
        return false;
    }
}
