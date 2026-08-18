using System.Net;
using System.Diagnostics;
using System.IO.Pipes;

namespace DiscordVpnLauncher;

internal static class DiscordController
{
    /// <summary>
    /// Pipe que o Discord cria quando termina de inicializar. E o sinal concreto de
    /// "Discord pronto" - nada de sleep fixo.
    /// </summary>
    private const string IpcPipeName = "discord-ipc-0";

    private const string PipeDirectory = @"\\.\pipe\";

    /// <summary>Permite apontar uma instalacao fora do caminho padrao.</summary>
    private const string EnvOverride = "DISCORD_VPN_LAUNCHER_DISCORD";

    private static readonly TimeSpan EsperaMortePorProcesso = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Encerra TODOS os processos do Discord. Obrigatorio: ele roda em varios
    /// processos e, se algum sobrar, o relancamento apenas foca a janela existente -
    /// sem nova inicializacao, sem re-captura de IP, e o launcher perde a razao de ser.
    /// </summary>
    public static int MatarTudo()
    {
        var nomes = new[] { "Discord", "DiscordPTB", "DiscordCanary" };
        var mortos = 0;

        foreach (var nome in nomes)
        {
            foreach (var processo in Process.GetProcessesByName(nome))
            {
                using (processo)
                {
                    try
                    {
                        processo.Kill(entireProcessTree: true);
                        processo.WaitForExit((int)EsperaMortePorProcesso.TotalMilliseconds);
                        mortos++;
                    }
                    catch (InvalidOperationException)
                    {
                        // saiu sozinho no meio do caminho
                    }
                    catch (Exception ex)
                    {
                        // Um Discord elevado nao pode ser morto por nos (integridade alta).
                        Log.Warn($"Nao foi possivel encerrar {nome} (pid {processo.Id}): {ex.Message}");
                    }
                }
            }
        }

        return mortos;
    }

    /// <summary>
    /// Espera o pipe desaparecer depois do kill. Sem isso, um pipe orfao da sessao
    /// anterior seria lido como "Discord ja esta pronto" na etapa de espera.
    /// </summary>
    public static bool EsperarPipeSumir(TimeSpan timeout)
    {
        var limite = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < limite)
        {
            if (!PipeExiste())
                return true;

            Thread.Sleep(200);
        }

        return !PipeExiste();
    }

    /// <summary>
    /// Lanca o Discord NAO-elevado. Como o orquestrador roda em integridade media
    /// (asInvoker no manifest), o Discord herda a mesma integridade e funciona
    /// normalmente - inclusive arrastar-e-soltar arquivos, que quebra quando o app
    /// roda como administrador.
    /// </summary>
    public static bool Lancar()
    {
        var alvo = LocalizarLauncher();
        if (alvo is null)
        {
            Log.Error("Instalacao do Discord nao encontrada. Defina " +
                      $"{EnvOverride} com o caminho do Update.exe ou do Discord.exe.");
            return false;
        }

        var (executavel, argumentos) = alvo.Value;

        var startInfo = new ProcessStartInfo
        {
            FileName = executavel,
            UseShellExecute = true, // sem verbo runas: herda a integridade do pai
            WorkingDirectory = Path.GetDirectoryName(executavel) ?? string.Empty,
        };

        foreach (var argumento in argumentos)
            startInfo.ArgumentList.Add(argumento);

        try
        {
            using var processo = Process.Start(startInfo);
            Log.Info($"Discord lancado via {Path.GetFileName(executavel)}.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Falha ao lancar o Discord: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Update.exe e o stub do Squirrel: ele resolve sozinho a versao atual em
    /// app-x.y.z, entao nao quebra a cada atualizacao do Discord. So caimos no
    /// Discord.exe direto se o stub nao existir.
    /// </summary>
    private static (string Executavel, string[] Argumentos)? LocalizarLauncher()
    {
        var custom = Environment.GetEnvironmentVariable(EnvOverride);
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
        {
            return Path.GetFileName(custom).Equals("Update.exe", StringComparison.OrdinalIgnoreCase)
                ? (custom, new[] { "--processStart", "Discord.exe" })
                : (custom, Array.Empty<string>());
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var pasta in new[] { "Discord", "DiscordPTB", "DiscordCanary" })
        {
            var raiz = Path.Combine(localAppData, pasta);
            if (!Directory.Exists(raiz))
                continue;

            var update = Path.Combine(raiz, "Update.exe");
            if (File.Exists(update))
                return (update, new[] { "--processStart", $"{pasta}.exe" });

            var direto = VersaoMaisRecente(raiz, $"{pasta}.exe");
            if (direto is not null)
                return (direto, Array.Empty<string>());
        }

        return null;
    }

    private static string? VersaoMaisRecente(string raiz, string nomeExe)
        => Directory.GetDirectories(raiz, "app-*")
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => Path.Combine(d, nomeExe))
            .FirstOrDefault(File.Exists);

    /// <summary>
    /// Espera o Discord ter uma conexao ESTABLISHED saindo pelo IP do tunel.
    ///
    /// Este e o sinal que importa, e o pipe de IPC nao serve para ele: o pipe sobe
    /// junto com o processo, muito antes de o app falar com o gateway - foi por isso
    /// que o launcher derrubava a VPN cedo demais e o Discord acabava registrando o
    /// IP real. Ter um socket estabelecido com origem no IP do tunel prova as duas
    /// coisas de uma vez: o Discord ja esta conversando, e esta conversando por
    /// dentro da VPN.
    /// </summary>
    public static bool EsperarTrafegoPeloTunel(IPAddress ipTunel, TimeSpan timeout)
    {
        var limite = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < limite)
        {
            var pids = PidsAtivos();

            if (pids.Count > 0)
            {
                foreach (var (pid, local) in TcpTable.Estabelecidas())
                {
                    if (pids.Contains(pid) && local.Equals(ipTunel))
                        return true;
                }
            }

            Thread.Sleep(500);
        }

        return false;
    }

    /// <summary>PIDs de todos os processos do Discord neste instante.</summary>
    private static HashSet<int> PidsAtivos()
    {
        var pids = new HashSet<int>();

        foreach (var nome in new[] { "Discord", "DiscordPTB", "DiscordCanary" })
        {
            foreach (var processo in Process.GetProcessesByName(nome))
            {
                using (processo)
                    pids.Add(processo.Id);
            }
        }

        return pids;
    }

    /// <summary>Espera o pipe de IPC aparecer; true = o processo do Discord subiu.</summary>
    public static bool EsperarProntidao(TimeSpan timeout)
    {
        var limite = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < limite)
        {
            if (PipeExiste())
                return true;

            Thread.Sleep(300);
        }

        return false;
    }

    private static bool PipeExiste()
    {
        // Enumerar e mais barato que abrir uma conexao; o fallback cobre o caso de a
        // enumeracao de \\.\pipe\ falhar (nomes de pipe com caracteres invalidos para
        // a API de arquivos).
        try
        {
            foreach (var pipe in Directory.GetFiles(PipeDirectory))
            {
                if (Path.GetFileName(pipe).Equals(IpcPipeName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch (Exception)
        {
            return TentarConectarPipe();
        }
    }

    private static bool TentarConectarPipe()
    {
        try
        {
            using var cliente = new NamedPipeClientStream(".", IpcPipeName, PipeDirection.InOut);
            cliente.Connect(50);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            // Pipe existe mas todas as instancias estao ocupadas: existe = pronto.
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
