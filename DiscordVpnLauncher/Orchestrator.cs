using System.Diagnostics;

namespace DiscordVpnLauncher;

/// <summary>
/// Modo orquestrador: o processo pai, NAO elevado. Faz tudo que nao exige admin e
/// delega ao broker so o que exige. Nunca mata o openvpn.exe diretamente - ele
/// sinaliza, e o broker (dono do processo, elevado) faz a limpeza.
/// </summary>
internal static class Orchestrator
{
    private const int QuantidadeCandidatos = 5;

    /// <summary>Sem nenhuma mudanca de status do broker por este tempo, desiste.</summary>
    private static readonly TimeSpan TimeoutVpn = TimeSpan.FromSeconds(45);

    /// <summary>Teto absoluto da fase de conexao, mesmo com o broker progredindo.</summary>
    private static readonly TimeSpan TimeoutVpnTotal = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TimeoutDiscord = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TimeoutIpinfo = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TimeoutTeardown = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TimeoutPipeSumir = TimeSpan.FromSeconds(5);

    private const string MutexName = @"Local\DiscordVpnLauncher-instancia-unica";

    public static async Task<int> RunAsync()
    {
        // Instancia unica: duas execucoes simultaneas brigariam pela mesma workDir e
        // pelas rotas do sistema.
        using var mutex = new Mutex(initiallyOwned: false, MutexName, out _);
        if (!mutex.WaitOne(TimeSpan.Zero))
        {
            Log.Error("O launcher ja esta em execucao.");
            return 7;
        }

        try
        {
            return await ExecutarSessaoAsync().ConfigureAwait(false);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static async Task<int> ExecutarSessaoAsync()
    {
        var paths = Paths.Default();
        Process? broker = null;

        if (ElevationHelper.IsElevated())
        {
            // Se o pai estiver elevado, o Discord herdaria integridade alta e rodaria
            // como admin (drag-and-drop quebrado, entre outros). Melhor avisar.
            Log.Warn("Este launcher foi aberto COMO ADMINISTRADOR. Feche e abra normalmente: " +
                     "o Discord herdaria privilegio de administrador.");
        }

        try
        {
            // 1. Ambiente + binarios embutidos
            paths.EnsureDirectories();
            paths.CleanWorkDir();

            var erroExtracao = paths.ExtractEmbeddedBinaries();
            if (erroExtracao is not null)
                return FinalizarComFalha(paths, broker, erroExtracao);

            Log.Step($"Binarios prontos em {paths.BinDir}.");

            // 2. Rede
            var paisOriginal = await ConsultarPaisAsync().ConfigureAwait(false);
            if (paisOriginal is null)
                return FinalizarComFalha(paths, broker, "sem conexao com a internet");

            Log.Step($"IP atual em {paisOriginal}.");

            // 3. Discord fora do ar antes de qualquer coisa
            var mortos = DiscordController.MatarTudo();
            Log.Step($"Discord encerrado ({mortos} processo(s)).");
            if (!DiscordController.EsperarPipeSumir(TimeoutPipeSumir))
                Log.Warn("O pipe discord-ipc-0 continua de pe; a deteccao de prontidao pode ser falso-positiva.");

            // 4-5. Lista VPNGate, filtro != BR e ranqueamento
            var csv = await VpnGateClient.DownloadCsvAsync(CancellationToken.None).ConfigureAwait(false);
            var relays = VpnGateClient.Parse(csv);
            Log.Step($"{relays.Count} relays na lista.");

            var candidatos = VpnGateClient.SelecionarCandidatos(relays, QuantidadeCandidatos);
            if (candidatos.Count == 0)
                return FinalizarComFalha(paths, broker, "nenhum relay fora do Brasil disponivel");

            Log.Step($"Candidatos: {string.Join(", ", candidatos.Select(c => c.CountryShort))}.");

            // 6-7. Configs em disco (o broker le a lista inteira e faz o retry sozinho)
            var gravados = VpnGateClient.GravarConfigs(paths, candidatos);
            if (gravados.Count == 0)
                return FinalizarComFalha(paths, broker, "nenhum config valido para gravar");

            // 8. Broker elevado - unico UAC da sessao
            Log.Step("Subindo o broker elevado (UAC)...");
            broker = ElevationHelper.LaunchBrokerElevated(paths, Environment.ProcessId);
            if (broker is null)
                return FinalizarComFalha(paths, broker, "elevacao recusada no UAC");

            // 9. Aguardar o tunel
            var status = AguardarStatusVpn(paths, broker);
            if (!status.StartsWith("connected:", StringComparison.Ordinal))
                return FinalizarComFalha(paths, broker, TraduzirStatus(status));

            var paisTunel = status["connected:".Length..];
            Log.Step($"VPN conectada ({paisTunel}).");

            // 10. Confirmar de fato o pais - o rotulo do VPNGate pode mentir
            var paisReal = await ConsultarPaisAsync().ConfigureAwait(false);
            if (paisReal is null)
                return FinalizarComFalha(paths, broker, "sem resposta do ipinfo com a VPN ativa");

            if (paisReal.Equals("BR", StringComparison.OrdinalIgnoreCase))
                return FinalizarComFalha(paths, broker, $"o IP continua brasileiro (rotulo dizia {paisTunel})");

            Log.Step($"IP confirmado fora do Brasil: {paisReal}.");

            // 11. Discord por baixo do tunel, sem elevacao
            if (!DiscordController.Lancar())
                return FinalizarComFalha(paths, broker, "falha ao lancar o Discord");

            // 12. Prontidao pelo pipe de IPC
            if (DiscordController.EsperarProntidao(TimeoutDiscord))
                Log.Step("Discord inicializou sob a VPN.");
            else
                Log.Warn($"O Discord nao sinalizou prontidao em {TimeoutDiscord.TotalSeconds:0}s; " +
                         "derrubando a VPN de qualquer forma.");

            // 13-14. Teardown
            PararVpn(paths, broker);
            Log.Step("Pronto. VPN derrubada, Discord rodando com o IP capturado.");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error($"Erro inesperado: {ex.Message}");
            return FinalizarComFalha(paths, broker, ex.Message);
        }
        finally
        {
            // Garantia final: nenhum caminho de saida deixa o tunel aberto. O broker
            // tambem vigia o PID deste processo, entao ha redundancia de proposito.
            SinalizarStop(paths);
            broker?.Dispose();
        }
    }

    /// <summary>
    /// Poll no status escrito pelo broker. Nao ha stdout para ler: "runas" exige
    /// UseShellExecute = true, que impede redirecionamento.
    ///
    /// O timeout e por INATIVIDADE, nao pelo total: o broker publica progresso
    /// (starting, trying:1, trying:2, ...) e o pior caso legitimo dele - criar o
    /// adaptador na primeira vez mais cinco candidatos de 20 s - passa de qualquer
    /// prazo fixo curto. Cada mudanca de status renova o prazo; o teto absoluto
    /// existe so para nao esperar para sempre.
    /// </summary>
    private static string AguardarStatusVpn(Paths paths, Process broker)
    {
        var tetoAbsoluto = DateTime.UtcNow + TimeoutVpnTotal;
        var limite = DateTime.UtcNow + TimeoutVpn;
        var ultimoStatus = string.Empty;

        while (DateTime.UtcNow < limite && DateTime.UtcNow < tetoAbsoluto)
        {
            var status = LerStatus(paths);

            if (status != ultimoStatus)
            {
                if (status.Length > 0)
                    Log.Info($"broker: {status}");

                ultimoStatus = status;
                limite = DateTime.UtcNow + TimeoutVpn;
            }

            if (status.StartsWith("connected:", StringComparison.Ordinal) ||
                status.StartsWith("failed", StringComparison.Ordinal))
                return status;

            if (broker.HasExited)
            {
                // Saiu sem publicar veredito: trava no proprio broker.
                var ultimo = LerStatus(paths);
                return ultimo.Length > 0 && !ultimo.StartsWith("trying", StringComparison.Ordinal)
                    ? ultimo
                    : "failed:broker-morreu";
            }

            Thread.Sleep(500);
        }

        return "failed:timeout";
    }

    private static string LerStatus(Paths paths)
    {
        try
        {
            return File.Exists(paths.VpnStatusFile)
                ? File.ReadAllText(paths.VpnStatusFile).Trim()
                : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty; // gravacao em andamento
        }
    }

    private static string TraduzirStatus(string status) => status switch
    {
        "failed:all" => "nenhum dos relays fora do Brasil aceitou conexao",
        "failed:timeout" => $"a VPN parou de progredir por {TimeoutVpn.TotalSeconds:0}s",
        "failed:sem-adaptador" => "nao foi possivel criar o adaptador de rede wintun",
        "failed:sem-openvpn" => "openvpn.exe/wintun.dll nao encontrados",
        "failed:sem-candidatos" => "o broker nao encontrou configs para tentar",
        "failed:sem-elevacao" => "o broker subiu sem privilegio de administrador",
        "failed:broker-morreu" => "o processo elevado encerrou sem conectar",
        "failed:excecao" => "erro interno no broker (ver work\\broker.log)",
        _ => status,
    };

    /// <summary>
    /// GET https://ipinfo.io/country. Cria um HttpClient novo a cada chamada de
    /// proposito: reaproveitar conexao depois de subir o tunel devolveria a resposta
    /// pela rota antiga.
    /// </summary>
    private static async Task<string?> ConsultarPaisAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeoutIpinfo };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordVpnLauncher/1.0");

            var resposta = await http.GetStringAsync("https://ipinfo.io/country").ConfigureAwait(false);
            var pais = resposta.Trim().ToUpperInvariant();
            return pais.Length is 2 ? pais : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"ipinfo indisponivel: {ex.Message}");
            return null;
        }
    }

    private static void PararVpn(Paths paths, Process? broker)
    {
        SinalizarStop(paths);

        if (broker is null)
            return;

        try
        {
            // O pai (integridade media) nao consegue matar o broker (integridade alta);
            // so da para pedir e esperar. Por isso o stop.signal existe.
            if (!broker.WaitForExit((int)TimeoutTeardown.TotalMilliseconds))
                Log.Warn("O broker nao encerrou no prazo; ele ainda derruba o tunel pelo watchdog.");
            else
                Log.Info("Broker encerrado e tunel desfeito.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Nao foi possivel aguardar o broker: {ex.Message}");
        }
    }

    private static void SinalizarStop(Paths paths)
    {
        try
        {
            Directory.CreateDirectory(paths.WorkDir);
            File.WriteAllText(paths.StopSignalFile, DateTime.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            Log.Error($"FALHA ao escrever stop.signal ({ex.Message}). " +
                      "O broker ainda derruba o tunel ao notar a morte deste processo.");
        }
    }

    /// <summary>
    /// Caminho unico de falha: derruba a VPN primeiro (para "continuar sem VPN"
    /// realmente significar IP real) e so depois pergunta ao usuario.
    /// </summary>
    private static int FinalizarComFalha(Paths paths, Process? broker, string motivo)
    {
        Log.Error($"Falha: {motivo}");
        PararVpn(paths, broker);

        var escolha = NativeMethods.ShowFailurePopup(motivo);

        if (escolha == FailureChoice.Fechar)
        {
            Log.Step("Usuario escolheu fechar; o Discord nao sera aberto.");
            return 1;
        }

        Log.Warn("Abrindo o Discord SEM VPN, com o IP real.");
        if (!DiscordController.Lancar())
            return 1;

        DiscordController.EsperarProntidao(TimeoutDiscord);
        return 1;
    }
}
