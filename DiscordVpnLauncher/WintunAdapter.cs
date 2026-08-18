using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DiscordVpnLauncher;

/// <summary>
/// Adaptador de rede wintun criado pelo proprio wintun.dll, via P/Invoke.
///
/// Por que nao o tapctl.exe (que vem no MSI do OpenVPN): ele cria adaptadores pela
/// SETUPAPI (DiInstallDevice), o que exige o driver ja presente no driver store da
/// maquina. Em um Windows que nunca teve OpenVPN instalado, isso falha com
/// 0xE0000203 - e instalar o driver e exatamente o que este projeto quer evitar.
///
/// O wintun.dll, por outro lado, carrega o driver assinado que ele mesmo embute e o
/// instala sob demanda na primeira criacao de adaptador (mesmo mecanismo do
/// WireGuard para Windows). Precisa de elevacao, e por isso vive dentro do broker.
///
/// O adaptador tem o tempo de vida do HANDLE: fechar o handle remove o adaptador.
/// Isso e uma vantagem aqui - se o broker morrer de qualquer maneira, o Windows
/// fecha o handle e o adaptador desaparece junto, sem deixar lixo de rede.
/// </summary>
internal sealed class WintunAdapter : IDisposable
{
    /// <summary>Nome do adaptador; e o que o OpenVPN recebe em --dev-node.</summary>
    public const string Nome = "DiscordVpnLauncher";

    private static LoggerCallback? _loggerVivo;

    private readonly IntPtr _biblioteca;
    private readonly IntPtr _adaptador;
    private readonly CloseAdapterDelegate _fechar;
    private bool _fechado;

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
    private delegate IntPtr CreateAdapterDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string nome,
        [MarshalAs(UnmanagedType.LPWStr)] string tipoTunel,
        IntPtr guidSolicitado);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CloseAdapterDelegate(IntPtr adaptador);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetRunningDriverVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetLoggerDelegate(LoggerCallback callback);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate void LoggerCallback(
        int nivel, ulong timestamp, [MarshalAs(UnmanagedType.LPWStr)] string mensagem);

    private WintunAdapter(IntPtr biblioteca, IntPtr adaptador, CloseAdapterDelegate fechar)
    {
        _biblioteca = biblioteca;
        _adaptador = adaptador;
        _fechar = fechar;
    }

    /// <summary>
    /// Cria o adaptador. Na primeira execucao da maquina isso instala o driver do
    /// wintun e pode levar alguns segundos.
    /// </summary>
    /// <returns>null se nao foi possivel criar (motivo ja registrado no log).</returns>
    public static WintunAdapter? Criar(Paths paths)
    {
        IntPtr biblioteca;
        try
        {
            // Caminho absoluto: o wintun.dll fica em bin\, nao ao lado do executavel
            // (que em single-file e uma pasta temporaria de extracao).
            biblioteca = NativeLibrary.Load(paths.WintunDll);
        }
        catch (Exception ex)
        {
            Log.Error($"Nao foi possivel carregar o wintun.dll: {ex.Message}");
            return null;
        }

        try
        {
            // Encaminha o diagnostico interno do wintun (inclusive falhas de
            // instalacao do driver) para o broker.log.
            var setLogger = Obter<SetLoggerDelegate>(biblioteca, "WintunSetLogger");
            _loggerVivo = Logar; // guardado em campo estatico: o GC nao pode coletar
            setLogger(_loggerVivo);

            var criar = Obter<CreateAdapterDelegate>(biblioteca, "WintunCreateAdapter");
            var fechar = Obter<CloseAdapterDelegate>(biblioteca, "WintunCloseAdapter");

            Log.Step($"Criando adaptador wintun '{Nome}' (instala o driver na 1a vez)...");
            var adaptador = criar(Nome, Nome, IntPtr.Zero);

            if (adaptador == IntPtr.Zero)
            {
                var erro = Marshal.GetLastWin32Error();
                Log.Error($"WintunCreateAdapter falhou (erro Win32 {erro}: " +
                          $"{new System.ComponentModel.Win32Exception(erro).Message}).");
                NativeLibrary.Free(biblioteca);
                return null;
            }

            var versao = Obter<GetRunningDriverVersionDelegate>(biblioteca, "WintunGetRunningDriverVersion")();
            Log.Info($"Adaptador criado; driver wintun {versao >> 16}.{versao & 0xFFFF}.");

            return new WintunAdapter(biblioteca, adaptador, fechar);
        }
        catch (Exception ex)
        {
            Log.Error($"Falha ao criar o adaptador wintun: {ex.Message}");
            NativeLibrary.Free(biblioteca);
            return null;
        }
    }

    private static T Obter<T>(IntPtr biblioteca, string exportacao) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(biblioteca, exportacao));

    private static void Logar(int nivel, ulong timestamp, string mensagem)
    {
        var texto = $"wintun: {mensagem}";

        switch (nivel)
        {
            case 2: // WINTUN_LOG_ERR
                Log.Error(texto);
                break;
            case 1: // WINTUN_LOG_WARN
                Log.Warn(texto);
                break;
            default:
                Log.Info(texto);
                break;
        }
    }

    /// <summary>
    /// IP que o OpenVPN configurou no nosso adaptador, ou null se ele ainda nao
    /// existe. Consultado pelo PAI, nao pelo broker: e com ele que o orquestrador
    /// reconhece quais conexoes do Discord estao saindo por dentro do tunel. Ler a
    /// lista de interfaces nao exige elevacao.
    /// </summary>
    public static IPAddress? EnderecoIpv4()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.Name.Equals(Nome, StringComparison.OrdinalIgnoreCase)
                            || i.Description.Contains(Nome, StringComparison.OrdinalIgnoreCase));

            foreach (var placa in interfaces)
            {
                var endereco = placa.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);

                if (endereco is not null)
                    return endereco.Address;
            }
        }
        catch (NetworkInformationException)
        {
            // sinal de prontidao; nunca motivo para abortar
        }

        return null;
    }

    /// <summary>Fecha o handle, o que remove o adaptador e as rotas presas a ele.</summary>
    public void Dispose()
    {
        if (_fechado)
            return;

        _fechado = true;

        try
        {
            _fechar(_adaptador);
            Log.Info($"Adaptador '{Nome}' removido.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Falha ao remover o adaptador: {ex.Message}");
        }
        finally
        {
            NativeLibrary.Free(_biblioteca);
        }
    }
}
