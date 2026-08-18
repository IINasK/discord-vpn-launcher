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
    /// PIDs que sao nossos, nunca do Discord de verdade.
    ///
    /// Existe porque o executavel se chama Discord.exe: sem esta lista,
    /// GetProcessesByName("Discord") devolveria o proprio launcher e o broker, e o
    /// MatarTudo se suicidaria antes de fazer qualquer coisa. Preenchida pelo
    /// orquestrador com o PID proprio e o do broker.
    /// </summary>
    private static readonly HashSet<int> PidsProprios = new();

    public static void IgnorarPid(int pid)
    {
        lock (PidsProprios)
            PidsProprios.Add(pid);
    }

    private static bool EhNosso(int pid)
    {
        lock (PidsProprios)
            return PidsProprios.Contains(pid);
    }

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
                    if (EhNosso(processo.Id))
                        continue; // somos nos: o launcher tambem se chama Discord.exe

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
    /// Como o Discord seria lancado, sem lancar nada e sem perguntar. Serve ao modo
    /// --diagnostico, que existe para dar suporte a distancia: e a primeira coisa a
    /// pedir quando alguem diz "nao abre".
    /// </summary>
    public static string DescreverAlvo()
    {
        var alvo = LocalizarLauncher(permitirPergunta: false);

        if (alvo is null)
            return "NAO ENCONTRADO (o launcher vai pedir para voce apontar o arquivo)";

        var (executavel, argumentos) = alvo.Value;
        return argumentos.Length > 0
            ? $"{executavel} {string.Join(' ', argumentos)}"
            : executavel;
    }

    /// <summary>
    /// Descobre como lancar o Discord, nesta ordem:
    ///
    ///   1. variavel de ambiente (escape manual, para quem quer forcar);
    ///   2. escolha manual lembrada de uma execucao anterior;
    ///   3. REGISTRO - onde o Discord anota onde ele realmente esta;
    ///   4. o caminho padrao em %LocalAppData%;
    ///   5. perguntar ao usuario, e lembrar da resposta.
    ///
    /// O registro entra antes do caminho padrao de proposito: e ele que resolve
    /// instalacao em outro HD ou pasta personalizada, sem ninguem ter que
    /// configurar nada. Um campo fixo (no instalador, por exemplo) nao resolveria
    /// o mesmo problema: o valor congelaria e passaria a mentir assim que o
    /// Discord fosse reinstalado em outro lugar.
    ///
    /// Update.exe e o stub do Squirrel: ele resolve sozinho a versao atual em
    /// app-x.y.z, entao nao quebra a cada atualizacao do Discord. So caimos no
    /// Discord.exe direto se o stub nao existir.
    /// </summary>
    private static (string Executavel, string[] Argumentos)? LocalizarLauncher(bool permitirPergunta = true)
    {
        var custom = Environment.GetEnvironmentVariable(EnvOverride);
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
            return Montar(custom);

        var lembrado = LerEscolhaLembrada();
        if (lembrado is not null)
            return Montar(lembrado);

        foreach (var raiz in RaizesDoRegistro().Concat(RaizesPadrao()))
        {
            var achado = ProcurarNaRaiz(raiz);
            if (achado is not null)
                return achado;
        }

        return permitirPergunta ? PerguntarAoUsuario() : null;
    }

    /// <summary>
    /// Monta o par (executavel, argumentos): o stub do Squirrel precisa de
    /// --processStart, o exe direto nao leva argumento nenhum.
    /// </summary>
    private static (string, string[]) Montar(string caminho)
    {
        if (!Path.GetFileName(caminho).Equals("Update.exe", StringComparison.OrdinalIgnoreCase))
            return (caminho, Array.Empty<string>());

        // Deduz a variante (Discord / PTB / Canary) pelo nome da pasta que contem
        // o Update.exe, senao um usuario de PTB receberia --processStart Discord.exe.
        var pasta = Path.GetFileName(Path.GetDirectoryName(caminho) ?? string.Empty);
        var exe = pasta.StartsWith("Discord", StringComparison.OrdinalIgnoreCase)
            ? $"{pasta}.exe"
            : "Discord.exe";

        return (caminho, new[] { "--processStart", exe });
    }

    /// <summary>
    /// Pastas de instalacao segundo o proprio Discord. Duas fontes independentes,
    /// as duas escritas por ele onde quer que tenha sido instalado:
    ///
    ///   - a entrada de desinstalacao (InstallLocation);
    ///   - o handler do protocolo discord://, que aponta para o exe em uso.
    /// </summary>
    private static IEnumerable<string> RaizesDoRegistro()
    {
        foreach (var nome in new[] { "Discord", "DiscordPTB", "DiscordCanary" })
        {
            var chave = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{nome}";
            var local = LerRegistro(chave, "InstallLocation");

            if (!string.IsNullOrWhiteSpace(local))
                yield return local;
        }

        foreach (var protocolo in new[] { "discord", "discord-ptb", "discord-canary" })
        {
            var comando = LerRegistro($@"Software\Classes\{protocolo}\shell\open\command", null);
            var exe = ExtrairExecutavel(comando);

            if (exe is null)
                continue;

            // O comando aponta para ...\Discord\app-1.2.3\Discord.exe; a raiz (com o
            // Update.exe) fica um nivel acima da pasta app-*.
            var pastaApp = Path.GetDirectoryName(exe);
            var raiz = Path.GetDirectoryName(pastaApp);

            if (raiz is not null)
                yield return raiz;

            if (pastaApp is not null)
                yield return pastaApp;
        }
    }

    private static IEnumerable<string> RaizesPadrao()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var pasta in new[] { "Discord", "DiscordPTB", "DiscordCanary" })
            yield return Path.Combine(localAppData, pasta);
    }

    /// <summary>Procura o Update.exe (preferido) ou o exe direto dentro de uma raiz.</summary>
    private static (string Executavel, string[] Argumentos)? ProcurarNaRaiz(string raiz)
    {
        if (string.IsNullOrWhiteSpace(raiz) || !Directory.Exists(raiz))
            return null;

        var update = Path.Combine(raiz, "Update.exe");
        if (File.Exists(update))
            return Montar(update);

        foreach (var nome in new[] { "Discord", "DiscordPTB", "DiscordCanary" })
        {
            // A raiz pode ser a propria pasta app-*, quando veio do protocolo.
            var direto = Path.Combine(raiz, $"{nome}.exe");
            if (File.Exists(direto))
                return (direto, Array.Empty<string>());

            var maisRecente = VersaoMaisRecente(raiz, $"{nome}.exe");
            if (maisRecente is not null)
                return (maisRecente, Array.Empty<string>());
        }

        return null;
    }

    private static string? LerRegistro(string chave, string? valor)
    {
        try
        {
            using var sub = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(chave);
            return sub?.GetValue(valor ?? string.Empty) as string;
        }
        catch (Exception)
        {
            return null; // registro e uma pista, nunca um requisito
        }
    }

    /// <summary>
    /// Tira o caminho do exe de uma linha de comando do registro, do tipo
    /// "C:\...\Discord.exe" --url -- "%1".
    /// </summary>
    private static string? ExtrairExecutavel(string? comando)
    {
        if (string.IsNullOrWhiteSpace(comando))
            return null;

        var texto = comando.Trim();

        if (texto.StartsWith('"'))
        {
            var fim = texto.IndexOf('"', 1);
            if (fim > 1)
                texto = texto[1..fim];
        }
        else
        {
            var espaco = texto.IndexOf(' ');
            if (espaco > 0)
                texto = texto[..espaco];
        }

        return File.Exists(texto) ? texto : null;
    }

    /// <summary>
    /// Ultimo recurso: pede o arquivo ao usuario e guarda a resposta, para nao
    /// perguntar de novo a cada execucao. Cobre instalacao portatil ou qualquer
    /// caso em que o Discord nao deixou rastro no registro.
    /// </summary>
    private static (string Executavel, string[] Argumentos)? PerguntarAoUsuario()
    {
        Log.Warn("Instalacao do Discord nao encontrada automaticamente.");

        if (NativeMethods.ShowLocateDiscordPrompt() != LocateChoice.Localizar)
            return null;

        var escolhido = NativeMethods.EscolherExecutavel(
            "Selecione o Discord.exe (ou o Update.exe da pasta do Discord)",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        if (escolhido is null || !File.Exists(escolhido))
            return null;

        GravarEscolha(escolhido);
        Log.Step($"Discord localizado em {escolhido} (lembrado para as proximas vezes).");
        return Montar(escolhido);
    }

    /// <summary>
    /// Arquivo com a escolha manual. Fica na RAIZ da pasta de dados, nao em work\,
    /// que e limpa a cada sessao.
    /// </summary>
    private static string ArquivoDaEscolha => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiscordVpnLauncher", "discord-path.txt");

    private static string? LerEscolhaLembrada()
    {
        try
        {
            if (!File.Exists(ArquivoDaEscolha))
                return null;

            var caminho = File.ReadAllText(ArquivoDaEscolha).Trim();

            // Some sozinho se o Discord foi desinstalado ou movido: melhor voltar a
            // detectar do que insistir num caminho morto.
            if (File.Exists(caminho))
                return caminho;

            Paths.TryDelete(ArquivoDaEscolha);
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void GravarEscolha(string caminho)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ArquivoDaEscolha)!);
            File.WriteAllText(ArquivoDaEscolha, caminho);
        }
        catch (Exception ex)
        {
            Log.Warn($"Nao foi possivel lembrar o caminho do Discord: {ex.Message}");
        }
    }

    private static string? VersaoMaisRecente(string raiz, string nomeExe)
        => Directory.GetDirectories(raiz, "app-*")
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => Path.Combine(d, nomeExe))
            .FirstOrDefault(File.Exists);

    /// <summary>
    /// Espera uma conexao do Discord pelo tunel que SOBREVIVA por
    /// <paramref name="estabilidade"/>, e devolve quanto tempo isso levou.
    ///
    /// O pipe de IPC nao serve como sinal aqui: ele sobe junto com o processo, muito
    /// antes de o app falar com o gateway - era por isso que o launcher derrubava a
    /// VPN cedo demais e o Discord acabava registrando o IP real.
    ///
    /// A persistencia e o que importa. O Discord abre varias conexoes curtas no
    /// boot (API, CDN, assets) que nascem e morrem em menos de um segundo; a que
    /// fica de pe e a sessao do gateway, e ela so se mantem depois do login ter
    /// concluido - que e exatamente o momento em que o IP foi registrado. Uma
    /// conexao com origem no IP do tunel prova as duas coisas de uma vez: o Discord
    /// esta conversando, e a conversa sai por dentro da VPN.
    ///
    /// Observar isso vale mais do que esperar um tempo fixo generoso: encurta a
    /// janela de VPN de dezenas de segundos para o tempo real do login, e o usuario
    /// para de ficar preso com ping alto em call por causa de uma margem chutada.
    ///
    /// Cada socket e identificado por porta local + destino: se ele sumir da tabela,
    /// a contagem daquela conexao e descartada e recomeca em outra.
    /// </summary>
    /// <returns>Tempo ate a sessao firmar, ou null se estourou o timeout.</returns>
    public static TimeSpan? EsperarSessaoPeloTunel(
        IPAddress ipTunel, TimeSpan timeout, TimeSpan estabilidade)
    {
        var inicio = DateTime.UtcNow;
        var limite = inicio + timeout;
        var desdeQuando = new Dictionary<string, DateTime>();

        while (DateTime.UtcNow < limite)
        {
            var pids = PidsAtivos();
            var agora = DateTime.UtcNow;
            var vistasAgora = new HashSet<string>();

            foreach (var conexao in TcpTable.Estabelecidas())
            {
                if (!pids.Contains(conexao.Pid) || !conexao.Local.Equals(ipTunel))
                    continue;

                var chave = conexao.Chave;
                vistasAgora.Add(chave);

                if (!desdeQuando.TryGetValue(chave, out var desde))
                {
                    desde = agora;
                    desdeQuando[chave] = desde;
                }

                if (agora - desde >= estabilidade)
                    return agora - inicio;
            }

            // Conexoes que sumiram nao acumulam tempo: eram trafego de boot, nao a
            // sessao. Sem isto, uma sequencia de conexoes curtas somaria como se
            // fosse uma so persistente.
            foreach (var chave in desdeQuando.Keys.Where(k => !vistasAgora.Contains(k)).ToList())
                desdeQuando.Remove(chave);

            Thread.Sleep(500);
        }

        return null;
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
                {
                    // Sem isto, as consultas do proprio launcher ao ipinfo (que saem
                    // pelo tunel) seriam lidas como "o Discord ja esta conversando".
                    if (!EhNosso(processo.Id))
                        pids.Add(processo.Id);
                }
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
