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

    /// <summary>Prazo para o Discord abrir uma conexao saindo pelo IP do tunel.</summary>
    private static readonly TimeSpan TimeoutTrafegoDiscord = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Quanto uma conexao pelo tunel precisa sobreviver para valer como "sessao
    /// firmada". As conexoes de boot do Discord (API, CDN) duram menos de um
    /// segundo; a do gateway fica de pe, e so fica depois do login concluir.
    /// </summary>
    private static readonly TimeSpan EstabilidadeSessao = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Margem final, ja com a sessao firmada. Pequena de proposito: cada segundo
    /// aqui e um segundo de ping do Japao para o usuario.
    /// </summary>
    private static readonly TimeSpan FolgaPadrao = TimeSpan.FromSeconds(5);

    /// <summary>Ajuste da folga sem recompilar, em segundos.</summary>
    private const string EnvFolga = "DISCORD_VPN_LAUNCHER_ESPERA";

    /// <summary>
    /// Respiro fixo entre a confirmacao do pais e a checagem de estabilidade, para o
    /// tunel recem-criado terminar de assentar antes de ser cobrado.
    /// </summary>
    private static readonly TimeSpan EsperaEstabilizacaoPadrao = TimeSpan.FromSeconds(5);

    private const string EnvEstabilizacao = "DISCORD_VPN_LAUNCHER_ESTABILIZACAO";

    /// <summary>
    /// Checagens seguidas e bem-sucedidas exigidas antes de o Discord subir. Duas ja
    /// separam "conectou e ficou" de "conectou e caiu"; mais que isso e tempo de ping
    /// ruim cobrado do usuario sem informacao nova.
    /// </summary>
    private const int ChecagensEstabilidade = 2;

    private static readonly TimeSpan IntervaloEstabilidade = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Prazo total da estabilizacao. Uma checagem que falha nao condena a sessao - ela
    /// zera o contador e tenta de novo dentro desta janela, porque oscilar uma vez logo
    /// apos a troca de rotas e normal; nao estabilizar dentro dela e que nao e.
    /// </summary>
    private static readonly TimeSpan JanelaEstabilizacao = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Teto da espera pelo clique de desligar. Generoso porque o normal e o usuario
    /// clicar em segundos; ele existe para a VPN nao ficar de pe quando o popup e
    /// ignorado, nao para apressar ninguem.
    /// </summary>
    private static readonly TimeSpan TetoDesligamentoManual = TimeSpan.FromMinutes(10);

    private const string EnvTetoManual = "DISCORD_VPN_LAUNCHER_TETO_MANUAL";
    private static readonly TimeSpan TimeoutIpinfo = TimeSpan.FromSeconds(5);

    /// <summary>Prazo total para confirmar que o IP publico ficou fora do Brasil.</summary>
    private static readonly TimeSpan JanelaConfirmacaoIp = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IntervaloEntreTentativasIp = TimeSpan.FromSeconds(2);

    /// <summary>Respiro entre o fim do OpenVPN e a 1a consulta, para as rotas assentarem.</summary>
    private static readonly TimeSpan EsperaAssentarRotas = TimeSpan.FromSeconds(2);
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

        // O executavel se chama Discord.exe: sem marcar os PIDs proprios, o passo de
        // "matar todos os processos do Discord" mataria o proprio launcher.
        DiscordController.IgnorarPid(Environment.ProcessId);

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

            // Espelha o console em disco so depois da limpeza, senao o arquivo da
            // sessao atual seria apagado logo apos ser criado.
            Log.MirrorTo(paths.LauncherLog);

            var erroExtracao = paths.ExtractEmbeddedBinaries();
            if (erroExtracao is not null)
                return FinalizarComFalha(paths, broker, erroExtracao);

            Log.Step($"Binarios prontos em {paths.BinDir}.");

            // 2. Rede
            var paisOriginal = await ConsultarPaisAsync().ConfigureAwait(false);
            if (paisOriginal is null)
                return FinalizarComFalha(paths, broker, "sem conexão com a internet");

            Log.Step($"IP atual em {paisOriginal}.");

            // 3. Discord fora do ar antes de qualquer coisa.
            //
            // Com zero processos encerrados nao ha o que esperar: o pipe discord-ipc-0
            // pertence ao processo do Discord e morre junto com ele, entao sem Discord
            // aberto nao existe pipe orfao a sumir. A espera so gastaria segundos para
            // confirmar o que ja se sabe - e este e o caso comum de quem desativou o
            // inicio automatico, como o launcher pede.
            var mortos = DiscordController.MatarTudo();

            if (mortos == 0)
            {
                Log.Step("Nenhum Discord aberto; seguindo direto.");
            }
            else
            {
                Log.Step($"Discord encerrado ({mortos} processo(s)).");
                if (!DiscordController.EsperarPipeSumir(TimeoutPipeSumir))
                    Log.Warn("O pipe discord-ipc-0 continua de pe; a deteccao de prontidao pode ser falso-positiva.");
            }

            // 4-5. Lista VPNGate, filtro != BR e ranqueamento
            var csv = await VpnGateClient.DownloadCsvAsync(CancellationToken.None).ConfigureAwait(false);
            var relays = VpnGateClient.Parse(csv);
            Log.Step($"{relays.Count} relays na lista.");

            var candidatos = VpnGateClient.SelecionarCandidatos(relays, QuantidadeCandidatos);
            if (candidatos.Count == 0)
                return FinalizarComFalha(paths, broker, "nenhum servidor fora do Brasil disponível");

            Log.Step($"Candidatos: {string.Join(", ", candidatos.Select(c => c.CountryShort))}.");

            // 6-7. Configs em disco (o broker le a lista inteira e faz o retry sozinho)
            var gravados = VpnGateClient.GravarConfigs(paths, candidatos);
            if (gravados.Count == 0)
                return FinalizarComFalha(paths, broker, "nenhuma configuração válida para gravar");

            // 8. Broker elevado - unico UAC da sessao
            Log.Step("Subindo o broker elevado (UAC)...");
            broker = ElevationHelper.LaunchBrokerElevated(paths, Environment.ProcessId);
            if (broker is null)
                return FinalizarComFalha(paths, broker, "permissão de administrador recusada");

            DiscordController.IgnorarPid(broker.Id); // tambem se chama Discord.exe

            // 9. Aguardar o tunel
            var status = AguardarStatusVpn(paths, broker);
            if (!status.StartsWith("connected:", StringComparison.Ordinal))
                return FinalizarComFalha(paths, broker, TraduzirStatus(status));

            var paisTunel = status["connected:".Length..];
            Log.Step($"VPN conectada ({paisTunel}).");

            // 10. Confirmar de fato o pais - o rotulo do VPNGate pode mentir.
            //
            // Uma tentativa unica aqui e cedo demais: no instante em que o OpenVPN
            // escreve "Initialization Sequence Completed" as rotas e o DNS acabaram de
            // ser trocados, e a primeira requisicao costuma morrer no socket (ou sair
            // ainda pela rota antiga e responder BR). Por isso a confirmacao insiste
            // dentro de uma janela em vez de decidir pelo primeiro resultado.
            var (paisReal, detalhe) = await ConfirmarPaisAsync(JanelaConfirmacaoIp).ConfigureAwait(false);
            if (paisReal is null)
                return FinalizarComFalha(paths, broker,
                    $"sem resposta dos serviços de verificação de IP ({detalhe})");

            if (paisReal.Equals("BR", StringComparison.OrdinalIgnoreCase))
                return FinalizarComFalha(paths, broker,
                    $"o IP permaneceu brasileiro após {JanelaConfirmacaoIp.TotalSeconds:0} segundos " +
                    $"(o servidor estava identificado como {paisTunel})");

            Log.Step($"IP confirmado fora do Brasil: {paisReal}.");

            // 10b. Estabilizacao. Uma confirmacao unica prova que o tunel chegou a
            // funcionar, nao que ele esta firme: relay do VPNGate que cai nos primeiros
            // segundos, rota que ainda oscila e adaptador que perde o IP acontecem
            // depois do "Initialization Sequence Completed". Lancar o Discord em cima
            // disso e o pior cenario - ele registra o IP real e nao ha segunda chance
            // sem matar e relancar.
            var (estavel, motivoInstabilidade) =
                await EstabilizarTunelAsync(paisReal).ConfigureAwait(false);

            if (!estavel)
                return FinalizarComFalha(paths, broker, motivoInstabilidade);

            // 11. Discord por baixo do tunel, sem elevacao
            if (!DiscordController.Lancar())
                return FinalizarComFalha(paths, broker, "não foi possível iniciar o Discord");

            // 12. Prontidao. O pipe de IPC diz apenas que o PROCESSO subiu - ele
            // aparece segundos antes de o app falar com o gateway do Discord, e era
            // por isso que a VPN caia antes da captura do IP acontecer.
            if (DiscordController.EsperarProntidao(TimeoutDiscord))
                Log.Step("Processo do Discord de pe.");
            else
                Log.Warn($"O Discord nao sinalizou prontidao em {TimeoutDiscord.TotalSeconds:0}s; " +
                         "seguindo assim mesmo.");

            EsperarCapturaDeIp();

            // 13. O desligamento e do usuario: so ele ve se o Discord ja carregou por
            // inteiro na tela. O teto de tempo existe para o invariante do tunel nunca
            // ficar refem de um clique que pode nunca vir.
            AguardarComandoDeDesligar();

            // 14. Teardown
            PararVpn(paths, broker);
            Log.Step("Pronto: VPN desligada, ping normal. O Discord ficou com o IP capturado - " +
                     "pode entrar em call.");
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
        "failed:all" => "nenhum dos servidores fora do Brasil aceitou a conexão",
        "failed:timeout" => $"a VPN parou de responder por {TimeoutVpn.TotalSeconds:0} segundos",
        "failed:sem-adaptador" => "não foi possível criar o adaptador de rede",
        "failed:sem-openvpn" => "arquivos do OpenVPN não encontrados",
        "failed:sem-candidatos" => "nenhuma configuração de servidor foi encontrada",
        "failed:sem-elevacao" => "o processo da VPN não obteve privilégio de administrador",
        "failed:broker-morreu" => "o processo da VPN foi encerrado antes de conectar",
        "failed:excecao" => "erro interno no processo da VPN (consulte work\\broker.log)",
        _ => status,
    };

    /// <summary>
    /// Servicos de geolocalizacao por IP, tentados nesta ordem. Ha mais de um porque
    /// um deles pode estar fora do ar, bloqueado no relay ou aplicando rate limit -
    /// e nesse caso a sessao inteira seria descartada por um motivo que nada tem a
    /// ver com a VPN. Todos respondem o codigo de duas letras em texto puro, exceto
    /// api.country.is, que responde JSON (ver ExtrairPais).
    /// </summary>
    private static readonly string[] ServicosDePais =
    {
        "https://ipinfo.io/country",
        "https://ifconfig.co/country-iso",
        "https://api.country.is/",
    };

    /// <summary>
    /// Uma consulta simples, usada antes de subir o tunel. Devolve null se nenhum
    /// servico respondeu.
    /// </summary>
    private static async Task<string?> ConsultarPaisAsync()
    {
        var (pais, erro) = await TentarPaisAsync().ConfigureAwait(false);

        if (pais is null)
            Log.Warn($"Nenhum servico de IP respondeu ({erro}).");

        return pais;
    }

    /// <summary>
    /// Insiste na consulta ate obter um pais fora do Brasil ou estourar a janela.
    ///
    /// Devolve (null, motivo) quando nenhum servico respondeu na janela inteira, e
    /// ("BR", motivo) quando responderam mas o IP continua brasileiro - sao falhas
    /// diferentes para o usuario, entao nao podem colapsar no mesmo retorno.
    /// </summary>
    private static async Task<(string? Pais, string Detalhe)> ConfirmarPaisAsync(TimeSpan janela)
    {
        // As rotas acabaram de ser reescritas; um respiro antes da primeira tentativa
        // evita queimar uma volta inteira em erro de socket.
        await Task.Delay(EsperaAssentarRotas).ConfigureAwait(false);

        var limite = DateTime.UtcNow + janela;
        var tentativa = 0;
        string? ultimoPais = null;
        var ultimoMotivo = "nenhuma tentativa concluida";

        while (true)
        {
            tentativa++;
            var (pais, erro) = await TentarPaisAsync().ConfigureAwait(false);

            if (pais is not null && !pais.Equals("BR", StringComparison.OrdinalIgnoreCase))
                return (pais, $"confirmado na tentativa {tentativa}");

            if (pais is not null)
            {
                ultimoPais = pais;
                ultimoMotivo = "as consultas continuam saindo pelo IP brasileiro";
                Log.Warn($"Tentativa {tentativa}: ainda BR; as rotas do tunel podem nao ter assentado.");
            }
            else
            {
                ultimoMotivo = erro;
                Log.Warn($"Tentativa {tentativa}: {erro}");
            }

            if (DateTime.UtcNow >= limite)
                return (ultimoPais, ultimoMotivo);

            await Task.Delay(IntervaloEntreTentativasIp).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifica que o tunel esta firme - nao apenas que chegou a subir - antes de o
    /// Discord ser lancado.
    ///
    /// Sao tres coisas conferidas, e nenhuma substitui a outra:
    ///
    /// 1. Um respiro fixo, para o adaptador e as rotas terminarem de assentar.
    /// 2. O adaptador do tunel continuar com IPv4. Se ele perdeu o endereco, o trafego
    ///    ja voltou para a placa real, e o Discord registraria o IP brasileiro.
    /// 3. Duas consultas de pais seguidas respondendo fora do BR. Relay do VPNGate que
    ///    cai nos primeiros segundos e comum, e o "connected" do OpenVPN nao volta
    ///    atras quando isso acontece.
    ///
    /// Uma falha isolada nao condena a sessao: o contador zera e a janela continua
    /// correndo. Estourar a janela, sim, e falha - vale mais o popup com as duas
    /// saidas do que um Discord aberto por um tunel que nao existe mais.
    /// </summary>
    private static async Task<(bool Estavel, string Motivo)> EstabilizarTunelAsync(string paisEsperado)
    {
        var espera = LerTempoEnv(EnvEstabilizacao, EsperaEstabilizacaoPadrao);

        if (espera > TimeSpan.Zero)
        {
            Log.Info($"Deixando o tunel assentar por {espera.TotalSeconds:0}s antes de abrir o Discord.");
            await Task.Delay(espera).ConfigureAwait(false);
        }

        var limite = DateTime.UtcNow + JanelaEstabilizacao;
        var seguidas = 0;
        var ultimoMotivo = "o túnel não se firmou a tempo";

        while (true)
        {
            if (WintunAdapter.EnderecoIpv4() is null)
            {
                seguidas = 0;
                ultimoMotivo = "o adaptador da VPN ficou sem endereço IP";
                Log.Warn("O adaptador do tunel esta sem IPv4; o trafego pode ter voltado para a rede real.");
            }
            else
            {
                var (pais, erro) = await TentarPaisAsync().ConfigureAwait(false);

                if (pais is null)
                {
                    seguidas = 0;
                    ultimoMotivo = $"a verificação de IP parou de responder pelo túnel ({erro})";
                    Log.Warn($"Estabilidade: sem resposta ({erro}).");
                }
                else if (pais.Equals("BR", StringComparison.OrdinalIgnoreCase))
                {
                    seguidas = 0;
                    ultimoMotivo = "o tráfego voltou a sair pelo IP brasileiro logo após a conexão";
                    Log.Warn("Estabilidade: a saida voltou a ser BR.");
                }
                else
                {
                    seguidas++;

                    if (!pais.Equals(paisEsperado, StringComparison.OrdinalIgnoreCase))
                        Log.Warn($"A saida mudou de {paisEsperado} para {pais}; segue valendo (nao e BR).");

                    Log.Info($"Estabilidade {seguidas}/{ChecagensEstabilidade}: saindo por {pais}.");

                    if (seguidas >= ChecagensEstabilidade)
                    {
                        Log.Step("Conexao com a VPN estabilizada; abrindo o Discord.");
                        return (true, string.Empty);
                    }
                }
            }

            if (DateTime.UtcNow >= limite)
                return (false, ultimoMotivo);

            await Task.Delay(IntervaloEstabilidade).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Uma volta pelos servicos, devolvendo o primeiro que responder. Cria um
    /// HttpClient novo a cada chamada de proposito: reaproveitar conexao depois de
    /// subir o tunel devolveria a resposta pela rota antiga.
    /// </summary>
    private static async Task<(string? Pais, string Erro)> TentarPaisAsync()
    {
        var erros = new List<string>();

        foreach (var url in ServicosDePais)
        {
            var host = new Uri(url).Host;

            try
            {
                using var http = new HttpClient { Timeout = TimeoutIpinfo };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordVpnLauncher/1.0");

                var resposta = await http.GetStringAsync(url).ConfigureAwait(false);
                var pais = ExtrairPais(resposta);

                if (pais is not null)
                    return (pais, string.Empty);

                erros.Add($"{host}: resposta inesperada");
            }
            catch (Exception ex)
            {
                erros.Add($"{host}: {MensagemRaiz(ex)}");
            }
        }

        return (null, string.Join("; ", erros));
    }

    /// <summary>
    /// Aceita tanto o texto puro ("JP") quanto o JSON do api.country.is
    /// ({"ip":"...","country":"JP"}).
    /// </summary>
    private static string? ExtrairPais(string resposta)
    {
        var texto = resposta.Trim();

        if (texto.Length is 2 && texto.All(char.IsAsciiLetter))
            return texto.ToUpperInvariant();

        var match = System.Text.RegularExpressions.Regex.Match(
            texto, "\"country\"\\s*:\\s*\"([A-Za-z]{2})\"");

        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// A mensagem util fica na excecao mais interna: HttpRequestException envolve a
    /// SocketException, e e o texto dela ("Nenhum host conhecido", "conexao
    /// recusada") que diz se o problema foi DNS ou rota.
    /// </summary>
    private static string MensagemRaiz(Exception ex)
    {
        var atual = ex;
        while (atual.InnerException is not null)
            atual = atual.InnerException;

        // Em timeout, o HttpClient lanca TaskCanceledException com TimeoutException
        // dentro, e a mensagem crua ("A operacao foi cancelada") nao diz nada.
        return atual is TimeoutException or OperationCanceledException
            ? $"sem resposta em {TimeoutIpinfo.TotalSeconds:0}s"
            : atual.Message;
    }

    /// <summary>
    /// Segura o tunel ate o Discord ter de fato registrado o IP nos servidores dele.
    ///
    /// Sao duas esperas, e nenhuma e decorativa:
    ///
    /// 1. Uma conexao ESTABLISHED do Discord com origem no IP do adaptador do tunel.
    ///    Prova que o app passou do "processo subiu" para "esta conversando", e que a
    ///    conversa sai por dentro da VPN.
    /// 2. Uma folga fixa depois disso, porque o handshake de login continua por mais
    ///    alguns segundos apos o primeiro socket abrir - e e no login que o IP e
    ///    fotografado.
    ///
    /// Nenhuma das duas e fatal: se o sinal nao vier, o launcher segue para o
    /// teardown do mesmo jeito (o invariante e a VPN nunca ficar aberta).
    /// </summary>
    private static void EsperarCapturaDeIp()
    {
        var ipTunel = WintunAdapter.EnderecoIpv4();

        if (ipTunel is null)
        {
            Log.Warn("Nao foi possivel identificar o IP do tunel; " +
                     "sem como confirmar que o Discord saiu por ele.");
        }
        else
        {
            var levou = DiscordController.EsperarSessaoPeloTunel(
                ipTunel, TimeoutTrafegoDiscord, EstabilidadeSessao);

            if (levou is not null)
                Log.Step($"Sessao do Discord firmada pelo tunel em {levou.Value.TotalSeconds:0.#}s " +
                         $"(origem {ipTunel}) - o IP ja foi registrado.");
            else
                Log.Warn($"Em {TimeoutTrafegoDiscord.TotalSeconds:0}s o Discord nao manteve " +
                         $"nenhuma conexao pelo tunel ({ipTunel}). O IP capturado pode ser o real.");
        }

        var folga = FolgaAposConexao();
        if (folga > TimeSpan.Zero)
            AguardarComContagem(folga);
    }

    /// <summary>
    /// Espera a folga final mostrando quanto falta.
    ///
    /// A contagem nao e enfeite: enquanto o tunel esta de pe, TODO o trafego do
    /// usuario passa pelo relay (Japao, tipicamente) e o ping em call fica
    /// impraticavel. Sem saber quanto falta, a pessoa entra na call, acha que
    /// travou e sai. Vai direto ao console, sem passar pelo Log, para nao encher o
    /// launcher.log de uma linha por segundo.
    /// </summary>
    private static void AguardarComContagem(TimeSpan folga)
    {
        var fim = DateTime.UtcNow + folga;

        while (true)
        {
            var restante = fim - DateTime.UtcNow;
            if (restante <= TimeSpan.Zero)
                break;

            Escrever($"\r   VPN ainda ativa por {restante.TotalSeconds:0}s - espere para entrar em call.   ");
            Thread.Sleep(Math.Min(500, (int)restante.TotalMilliseconds + 1));
        }

        Escrever("\r" + new string(' ', 70) + "\r");
    }

    private static void Escrever(string texto)
    {
        try
        {
            Console.Write(texto);
        }
        catch (IOException)
        {
            // console redirecionado ou fechado: a contagem e cosmetica
        }
    }

    /// <summary>
    /// Quanto tempo manter o tunel depois de o Discord comecar a falar. O default
    /// vem de teste manual (VPN de pe ate o app carregar por inteiro); a variavel de
    /// ambiente existe para ajustar sem recompilar, ja que o tempo certo depende da
    /// maquina e da conexao.
    /// </summary>
    private static TimeSpan FolgaAposConexao() => LerTempoEnv(EnvFolga, FolgaPadrao);

    /// <summary>
    /// Le um tempo em segundos de variavel de ambiente, caindo no padrao quando o valor
    /// e ausente ou nao faz sentido. O teto de 3600 s nao e arbitrario: acima disso o
    /// valor quase certamente e engano (milissegundos digitados como segundos), e o
    /// preco de aceitar seria uma VPN de pe por horas.
    /// </summary>
    private static TimeSpan LerTempoEnv(string nome, TimeSpan padrao)
    {
        var bruto = Environment.GetEnvironmentVariable(nome);

        if (int.TryParse(bruto, out var segundos) && segundos is >= 0 and <= 3600)
            return TimeSpan.FromSeconds(segundos);

        if (!string.IsNullOrWhiteSpace(bruto))
            Log.Warn($"{nome}='{bruto}' ignorado (esperado: 0 a 3600 segundos).");

        return padrao;
    }

    /// <summary>
    /// Popup que segura o tunel ate o usuario mandar desligar.
    ///
    /// Quem esta na frente da tela e a unica parte do sistema que sabe se o Discord
    /// terminou de carregar - a heuristica de EsperarCapturaDeIp acerta o caso comum,
    /// mas nao ve uma tela de login, um update em andamento ou um 2FA. O clique fecha
    /// essa lacuna. O teto continua existindo porque a VPN nao pode ficar de pe
    /// esperando um clique que talvez nunca venha.
    /// </summary>
    private static void AguardarComandoDeDesligar()
    {
        var teto = LerTempoEnv(EnvTetoManual, TetoDesligamentoManual);

        if (teto <= TimeSpan.Zero)
        {
            Log.Info("Desligamento manual desativado; derrubando a VPN direto.");
            return;
        }

        Log.Step($"VPN ainda ligada. Clique em OK no aviso para desliga-la " +
                 $"(automatico em {teto.TotalMinutes:0.#} min).");

        if (NativeMethods.ShowTeardownPrompt(teto))
            Log.Step("Desligamento pedido pelo usuario.");
        else
            Log.Warn($"Sem resposta em {teto.TotalMinutes:0.#} min; desligando a VPN por conta propria.");
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
