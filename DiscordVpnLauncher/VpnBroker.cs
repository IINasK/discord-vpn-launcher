using System.Diagnostics;
using System.Text;

namespace DiscordVpnLauncher;

/// <summary>
/// Modo broker: a segunda instancia do proprio .exe, elevada, unica que toca no
/// openvpn.exe.
///
/// Ela existe por um detalhe do Windows: um processo de integridade media nao
/// consegue matar um processo de integridade alta. Se o filho elevado fosse o
/// proprio openvpn.exe, o pai nao-elevado nao conseguiria derruba-lo no fim e o
/// tunel ficaria aberto. Como o broker elevado e o dono do processo do OpenVPN,
/// ele consegue mata-lo e devolver as rotas.
///
/// Todo o retry entre relays acontece AQUI, para que a sessao inteira custe um
/// unico prompt de UAC.
/// </summary>
internal static class VpnBroker
{
    private static readonly TimeSpan TimeoutPorCandidato = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IntervaloVigilia = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan EsperaTeardown = TimeSpan.FromSeconds(5);

    private const string MarcadorSucesso = "Initialization Sequence Completed";

    /// <summary>Nome do adaptador wintun criado por este launcher, para podermos removê-lo depois.</summary>
    private const string NomeAdaptador = "DiscordVpnLauncher";

    private static readonly TimeSpan TimeoutTapCtl = TimeSpan.FromSeconds(60);

    /// <summary>Linhas do log que significam "este relay nao vai subir; passe para o proximo".</summary>
    private static readonly string[] MarcadoresFatais =
    {
        "Exiting due to fatal error",
        "AUTH_FAILED",
        "Cannot resolve host address",
        "Options error",
        "TLS Error: TLS handshake failed",
        "Connection reset, restarting",
        "All connections have been connect-retry-max",
    };

    public static int Run(string[] args)
    {
        // --broker <workDir> <parentPid> <binDir>
        if (args.Length < 4
            || !int.TryParse(args[2], out var parentPid))
        {
            Console.Error.WriteLine($"Uso: DiscordVpnLauncher {Program.BrokerFlag} <workDir> <parentPid> <binDir>");
            return 2;
        }

        var paths = Paths.FromWorkDir(args[1], args[3]);
        Log.MirrorTo(paths.BrokerLog);
        Log.Step($"Broker iniciado (pai={parentPid}, elevado={ElevationHelper.IsElevated()}).");

        Process? openvpn = null;

        try
        {
            if (!ElevationHelper.IsElevated())
            {
                // Nao deveria acontecer: so chegamos aqui via runas.
                EscreverStatus(paths, "failed:sem-elevacao");
                return 3;
            }

            if (!File.Exists(paths.OpenVpnExe) || !File.Exists(paths.WintunDll) || !File.Exists(paths.TapCtlExe))
            {
                Log.Error($"Binarios ausentes em {paths.BinDir}.");
                EscreverStatus(paths, "failed:sem-openvpn");
                return 4;
            }

            var candidatos = paths.EnumerateCandidates();
            if (candidatos.Count == 0)
            {
                Log.Error("Nenhum candidato .ovpn na pasta de trabalho.");
                EscreverStatus(paths, "failed:sem-candidatos");
                return 5;
            }

            EscreverStatus(paths, "starting");

            // O adaptador tem que existir ANTES do openvpn: ele nao cria um sozinho.
            if (!GarantirAdaptador(paths))
            {
                EscreverStatus(paths, "failed:sem-adaptador");
                return 7;
            }

            openvpn = ConectarPrimeiroQueSubir(paths, candidatos, out var pais);

            if (openvpn is null)
            {
                Log.Error($"Todos os {candidatos.Count} candidatos falharam.");
                EscreverStatus(paths, "failed:all");
                return 6;
            }

            EscreverStatus(paths, $"connected:{pais}");
            Log.Step($"Tunel ativo em {pais}. Vigiando pai e sinal de stop.");

            AguardarFimDaSessao(paths, parentPid, openvpn);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error($"Erro inesperado no broker: {ex}");
            EscreverStatus(paths, "failed:excecao");
            return 1;
        }
        finally
        {
            // Rede de seguranca: qualquer caminho de saida derruba o tunel.
            DerrubarOpenVpn(openvpn);
            RemoverAdaptador(paths);
            LimparArquivosSensiveis(paths);
            Log.Step("Broker encerrado.");
        }
    }

    /// <summary>
    /// Cria o adaptador wintun se ele ainda nao existir, via tapctl.exe.
    ///
    /// Isto e uma correcao sobre o plano original: matar o openvpn.exe NAO faz o
    /// adaptador desaparecer, e o openvpn tambem nao cria um. Quem cria e remove e o
    /// tapctl - que exige elevacao, e por isso mora aqui no broker. Como o broker ja
    /// esta elevado, chamar o tapctl nao gera um segundo UAC.
    /// </summary>
    private static bool GarantirAdaptador(Paths paths)
    {
        var (codigo, saida) = RodarTapCtl(paths, "list");

        if (codigo == 0 && saida.Contains(NomeAdaptador, StringComparison.OrdinalIgnoreCase))
        {
            // Sobrou de uma sessao anterior que nao conseguiu limpar; reaproveita.
            Log.Info($"Adaptador '{NomeAdaptador}' ja existe; reaproveitando.");
            return true;
        }

        Log.Step($"Criando adaptador wintun '{NomeAdaptador}'...");
        var (codigoCriacao, saidaCriacao) = RodarTapCtl(
            paths, $"create --hardware-id wintun --name \"{NomeAdaptador}\"");

        if (codigoCriacao == 0)
        {
            Log.Info($"Adaptador criado ({saidaCriacao.Trim()}).");
            return true;
        }

        Log.Error($"tapctl create falhou (codigo {codigoCriacao}): {saidaCriacao.Trim()}");
        return false;
    }

    /// <summary>
    /// Remove o adaptador no teardown, para o sistema voltar ao estado anterior -
    /// as rotas associadas a ele vao junto.
    /// </summary>
    private static void RemoverAdaptador(Paths paths)
    {
        if (!File.Exists(paths.TapCtlExe))
            return;

        var (codigo, saida) = RodarTapCtl(paths, $"delete \"{NomeAdaptador}\"");

        if (codigo == 0)
            Log.Info($"Adaptador '{NomeAdaptador}' removido.");
        else
            Log.Warn($"Nao foi possivel remover o adaptador (codigo {codigo}): {saida.Trim()}");
    }

    private static (int Codigo, string Saida) RodarTapCtl(Paths paths, string argumentos)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = paths.TapCtlExe,
                Arguments = argumentos,
                WorkingDirectory = paths.BinDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var processo = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start devolveu null.");

            var stdout = processo.StandardOutput.ReadToEndAsync();
            var stderr = processo.StandardError.ReadToEndAsync();

            if (!processo.WaitForExit((int)TimeoutTapCtl.TotalMilliseconds))
            {
                processo.Kill(entireProcessTree: true);
                return (-1, "tapctl travou e foi encerrado.");
            }

            return (processo.ExitCode,
                    stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>
    /// Tenta os candidatos em ordem e devolve o processo do OpenVPN do primeiro que
    /// completar a inicializacao. Mantido dentro do broker para nao gerar novo UAC.
    /// </summary>
    private static Process? ConectarPrimeiroQueSubir(
        Paths paths, IReadOnlyList<string> candidatos, out string pais)
    {
        pais = "??";

        for (var i = 0; i < candidatos.Count; i++)
        {
            var config = candidatos[i];
            var candidato = i + 1;
            var paisCandidato = VpnGateClient.LerPaisDoConfig(config);

            Log.Step($"Candidato {candidato}/{candidatos.Count} ({paisCandidato})...");
            EscreverStatus(paths, $"trying:{candidato}");

            Paths.TryDelete(paths.OpenVpnLog);

            Process? processo = null;
            try
            {
                processo = IniciarOpenVpn(paths, config);
            }
            catch (Exception ex)
            {
                Log.Error($"Nao foi possivel iniciar o openvpn.exe: {ex.Message}");
                return null; // problema no binario, nao no relay: tentar outro nao ajuda
            }

            if (EsperarInicializacao(paths.OpenVpnLog, processo))
            {
                Log.Info($"Candidato {candidato} conectou.");
                pais = paisCandidato;
                return processo;
            }

            Log.Warn($"Candidato {candidato} falhou.");
            DerrubarOpenVpn(processo);
            PreservarLogDeFalha(paths, candidato);
        }

        return null;
    }

    private static Process IniciarOpenVpn(Paths paths, string configPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = paths.OpenVpnExe,
            // wintun.dll tem que ser resolvido ao lado do openvpn.exe.
            WorkingDirectory = paths.BinDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);
        // Explicito por garantia, mesmo sendo o default no 2.6.
        startInfo.ArgumentList.Add("--windows-driver");
        startInfo.ArgumentList.Add("wintun");
        // Aponta para o adaptador que criamos, em vez de deixar o openvpn escolher
        // entre adaptadores que possam existir na maquina por outros motivos.
        startInfo.ArgumentList.Add("--dev-node");
        startInfo.ArgumentList.Add(NomeAdaptador);
        startInfo.ArgumentList.Add("--log");
        startInfo.ArgumentList.Add(paths.OpenVpnLog);
        startInfo.ArgumentList.Add("--verb");
        startInfo.ArgumentList.Add("3");

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start devolveu null.");
    }

    /// <summary>
    /// Espera o sinal concreto de VPN pronta no log - sem sleep fixo. Sai antes do
    /// timeout se aparecer erro fatal ou se o processo morrer.
    /// </summary>
    private static bool EsperarInicializacao(string logPath, Process processo)
    {
        var limite = DateTime.UtcNow + TimeoutPorCandidato;

        while (DateTime.UtcNow < limite)
        {
            var log = LerLogTolerante(logPath);

            if (log.Contains(MarcadorSucesso, StringComparison.Ordinal))
                return true;

            var fatal = MarcadoresFatais.FirstOrDefault(m => log.Contains(m, StringComparison.Ordinal));
            if (fatal is not null)
            {
                Log.Info($"Log reportou: {fatal}");
                return false;
            }

            if (processo.HasExited)
            {
                Log.Info($"openvpn.exe saiu com codigo {processo.ExitCode} antes de conectar.");
                return false;
            }

            Thread.Sleep(400);
        }

        Log.Info($"Timeout de {TimeoutPorCandidato.TotalSeconds:0}s para este candidato.");
        return false;
    }

    /// <summary>
    /// Le o log enquanto o OpenVPN o mantem aberto para escrita (por isso o
    /// FileShare permissivo). Ausencia do arquivo e normal nos primeiros instantes.
    /// </summary>
    private static string LerLogTolerante(string logPath)
    {
        try
        {
            using var stream = new FileStream(
                logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
        catch (DirectoryNotFoundException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>Bloqueia ate o pai pedir stop ou morrer.</summary>
    private static void AguardarFimDaSessao(Paths paths, int parentPid, Process openvpn)
    {
        while (true)
        {
            if (File.Exists(paths.StopSignalFile))
            {
                Log.Step("stop.signal recebido.");
                return;
            }

            if (!PaiEstaVivo(parentPid))
            {
                // Watchdog: se o pai crashar sem escrever o sinal, o tunel morre igual.
                Log.Warn($"Pai (pid {parentPid}) desapareceu; derrubando o tunel.");
                return;
            }

            if (openvpn.HasExited)
            {
                Log.Warn($"openvpn.exe caiu sozinho (codigo {openvpn.ExitCode}).");
                return;
            }

            Thread.Sleep(IntervaloVigilia);
        }
    }

    private static bool PaiEstaVivo(int pid)
    {
        try
        {
            using var processo = Process.GetProcessById(pid);
            return !processo.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // pid nao existe mais
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void DerrubarOpenVpn(Process? processo)
    {
        if (processo is null)
            return;

        try
        {
            if (!processo.HasExited)
            {
                // Sem interface de management, Kill e o caminho. Matar o openvpn
                // desfaz as rotas que ele instalou, mas NAO remove o adaptador -
                // isso e trabalho do RemoverAdaptador, chamado logo depois.
                processo.Kill(entireProcessTree: true);
                processo.WaitForExit((int)EsperaTeardown.TotalMilliseconds);
            }

            Log.Info("openvpn.exe encerrado.");
        }
        catch (InvalidOperationException)
        {
            // ja havia saido
        }
        catch (Exception ex)
        {
            Log.Error($"Falha ao encerrar o openvpn.exe: {ex.Message}");
        }
        finally
        {
            processo.Dispose();
        }
    }

    /// <summary>
    /// Guarda o log do candidato que falhou para diagnostico. Nao contem segredo -
    /// diferente dos .ovpn, que trazem chave privada inline e sao sempre apagados.
    /// </summary>
    private static void PreservarLogDeFalha(Paths paths, int candidato)
    {
        try
        {
            if (!File.Exists(paths.OpenVpnLog))
                return;

            var destino = Path.Combine(paths.WorkDir, $"openvpn-cand{candidato}-falhou.log");
            File.Move(paths.OpenVpnLog, destino, overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    private static void LimparArquivosSensiveis(Paths paths)
    {
        foreach (var config in paths.EnumerateCandidates())
            Paths.TryDelete(config); // chave privada inline: nao deixar em disco

        Paths.TryDelete(paths.StopSignalFile);
    }

    /// <summary>
    /// Escreve o status via arquivo temporario + move, para o pai nunca ler um
    /// conteudo pela metade.
    /// </summary>
    private static void EscreverStatus(Paths paths, string status)
    {
        try
        {
            Directory.CreateDirectory(paths.WorkDir);
            var temp = paths.VpnStatusFile + ".tmp";
            File.WriteAllText(temp, status, new UTF8Encoding(false));
            File.Move(temp, paths.VpnStatusFile, overwrite: true);
            Log.Info($"status = {status}");
        }
        catch (Exception ex)
        {
            Log.Error($"Falha ao escrever o status '{status}': {ex.Message}");
        }
    }
}
