using System.Text;

namespace DiscordVpnLauncher;

internal sealed record VpnGateRelay(
    string HostName,
    string CountryShort,
    string CountryLong,
    long Score,
    int Ping,
    string ConfigBase64);

/// <summary>
/// Lista publica do VPNGate: CSV sem login e sem chave de API, onde cada linha traz
/// o .ovpn completo em base64 (CA/cert/key inline, self-contained).
/// </summary>
internal static class VpnGateClient
{
    private const string CountryBrasil = "BR";

    /// <summary>Marcador lido pelo broker para saber o pais do candidato (OpenVPN ignora linhas '#').</summary>
    public const string CountryMarker = "# vpngate-country=";

    private static readonly string[] ApiUrls =
    {
        "https://www.vpngate.net/api/iphone/",
        "http://www.vpngate.net/api/iphone/",
    };

    /// <summary>
    /// Opcoes que fazem o OpenVPN 2.6 abortar e que os configs (antigos) do VPNGate
    /// ainda trazem: as removidas da 2.5/2.6 viram "Unrecognized option", e
    /// explicit-exit-notify e fatal quando o relay usa proto tcp.
    /// </summary>
    private static readonly string[] OpcoesIncompativeis =
    {
        "ncp-disable",
        "keysize",
        "no-iv",
        "tls-remote",
        "comp-noadapt",
        "max-routes",
        "ns-cert-type",
        "explicit-exit-notify", // valido em UDP, fatal em TCP; o VPNGate mistura os dois
        "dev-node",             // o broker passa o proprio --dev-node; duplicar da conflito
    };

    public static async Task<string> DownloadCsvAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordVpnLauncher/1.0");

        Exception? ultimaFalha = null;

        foreach (var url in ApiUrls)
        {
            try
            {
                var csv = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(csv))
                    return csv;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                ultimaFalha = ex;
                Log.Warn($"Falha ao baixar a lista em {url}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            "Nao foi possivel baixar a lista do VPNGate.", ultimaFalha);
    }

    /// <summary>
    /// Parser deliberadamente tolerante (o formato do VPNGate ja mudou no passado):
    /// resolve colunas pelo NOME no cabecalho, pula linhas de comentario '*', e pega
    /// o base64 no ULTIMO campo em vez de num indice fixo - a coluna Message pode
    /// conter virgulas e deslocar tudo o que vem depois dela.
    /// </summary>
    public static IReadOnlyList<VpnGateRelay> Parse(string csv)
    {
        var relays = new List<VpnGateRelay>();
        Dictionary<string, int>? colunas = null;

        foreach (var rawLine in csv.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '﻿'); // U+FEFF: BOM na primeira linha

            if (line.Length == 0 || line[0] == '*')
                continue;

            if (line[0] == '#')
            {
                colunas = MapearCabecalho(line[1..]);
                continue;
            }

            if (colunas is null)
                continue; // dados antes do cabecalho: nao ha como interpretar

            var relay = ParseLinha(line, colunas);
            if (relay is not null)
                relays.Add(relay);
        }

        return relays;
    }

    private static Dictionary<string, int> MapearCabecalho(string header)
    {
        var mapa = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var campos = header.Split(',');

        for (var i = 0; i < campos.Length; i++)
        {
            var nome = campos[i].Trim();
            if (nome.Length > 0)
                mapa[nome] = i;
        }

        return mapa;
    }

    private static VpnGateRelay? ParseLinha(string line, Dictionary<string, int> colunas)
    {
        var campos = line.Split(',');
        if (campos.Length < 3)
            return null;

        // O .ovpn base64 e sempre o ultimo campo preenchido da linha.
        var configBase64 = campos.Reverse().FirstOrDefault(c => c.Trim().Length > 0)?.Trim();
        if (string.IsNullOrEmpty(configBase64) || configBase64.Length < 100)
            return null;

        var countryShort = Campo(campos, colunas, "CountryShort");
        if (countryShort.Length == 0)
            return null;

        return new VpnGateRelay(
            HostName: Campo(campos, colunas, "HostName"),
            CountryShort: countryShort.ToUpperInvariant(),
            CountryLong: Campo(campos, colunas, "CountryLong"),
            Score: long.TryParse(Campo(campos, colunas, "Score"), out var score) ? score : 0,
            Ping: int.TryParse(Campo(campos, colunas, "Ping"), out var ping) ? ping : int.MaxValue,
            ConfigBase64: configBase64);
    }

    private static string Campo(string[] campos, Dictionary<string, int> colunas, string nome)
        => colunas.TryGetValue(nome, out var i) && i < campos.Length ? campos[i].Trim() : string.Empty;

    /// <summary>
    /// Descarta relays brasileiros e ranqueia o resto. Score alto no VPNGate reflete
    /// banda + uptime acumulados; o ping entra so como desempate.
    /// </summary>
    public static IReadOnlyList<VpnGateRelay> SelecionarCandidatos(
        IEnumerable<VpnGateRelay> relays, int quantidade)
        => relays
            .Where(r => !r.CountryShort.Equals(CountryBrasil, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Ping)
            .Take(quantidade)
            .ToList();

    /// <summary>
    /// Decodifica cada candidato e grava work\candN.ovpn.
    /// </summary>
    /// <returns>Os candidatos efetivamente gravados, na mesma ordem dos arquivos.</returns>
    public static IReadOnlyList<VpnGateRelay> GravarConfigs(
        Paths paths, IReadOnlyList<VpnGateRelay> candidatos)
    {
        var gravados = new List<VpnGateRelay>();

        foreach (var candidato in candidatos)
        {
            string config;
            try
            {
                config = Encoding.UTF8.GetString(Convert.FromBase64String(candidato.ConfigBase64));
            }
            catch (FormatException)
            {
                Log.Warn($"Config base64 invalido em {candidato.HostName}; pulando.");
                continue;
            }

            if (!config.Contains("remote ", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"Config de {candidato.HostName} sem linha 'remote'; pulando.");
                continue;
            }

            var destino = paths.CandidateConfig(gravados.Count + 1);
            File.WriteAllText(destino, PrepararConfig(config, candidato), new UTF8Encoding(false));
            gravados.Add(candidato);
        }

        return gravados;
    }

    /// <summary>
    /// Ajusta o config do VPNGate para o OpenVPN 2.6 e anota o pais para o broker.
    /// </summary>
    private static string PrepararConfig(string config, VpnGateRelay relay)
    {
        var linhas = config.Replace("\r\n", "\n").Split('\n');
        var saida = new StringBuilder();

        saida.Append(CountryMarker).Append(relay.CountryShort).Append('\n');
        saida.Append("# vpngate-host=").Append(relay.HostName).Append('\n');

        var temDataCiphers = false;
        var usaCompressao = false;

        foreach (var linha in linhas)
        {
            var primeiraPalavra = linha.TrimStart().Split(' ', '\t').FirstOrDefault() ?? string.Empty;

            if (OpcoesIncompativeis.Contains(primeiraPalavra, StringComparer.OrdinalIgnoreCase))
                continue;

            if (primeiraPalavra.Equals("data-ciphers", StringComparison.OrdinalIgnoreCase))
                temDataCiphers = true;

            if (primeiraPalavra is "comp-lzo" or "compress")
                usaCompressao = true;

            saida.Append(linha).Append('\n');
        }

        saida.Append("\n# --- ajustes aplicados pelo DiscordVpnLauncher ---\n");

        if (!temDataCiphers)
        {
            // Os relays do VPNGate negociam cifras antigas; sem isto o 2.6 recusa.
            saida.Append("data-ciphers AES-256-GCM:AES-128-GCM:AES-256-CBC:AES-128-CBC\n");
            saida.Append("data-ciphers-fallback AES-128-CBC\n");
        }

        if (usaCompressao)
        {
            // O 2.6 desliga compressao por padrao; sem permitir explicitamente, o
            // comp-lzo herdado do config vira erro de framing.
            saida.Append("allow-compression asym\n");
        }

        // Falhar rapido em relay ruim: o broker parte para o proximo candidato em vez
        // de esperar o OpenVPN reciclar a conexao indefinidamente.
        saida.Append("connect-retry-max 2\n");
        saida.Append("resolv-retry 10\n");
        saida.Append("auth-nocache\n");

        return saida.ToString();
    }

    /// <summary>Le o pais anotado em um candidato ja gravado.</summary>
    public static string LerPaisDoConfig(string configPath)
    {
        try
        {
            foreach (var linha in File.ReadLines(configPath))
            {
                if (linha.StartsWith(CountryMarker, StringComparison.Ordinal))
                    return linha[CountryMarker.Length..].Trim();

                if (!linha.StartsWith('#'))
                    break; // marcador so existe no topo
            }
        }
        catch (IOException)
        {
        }

        return "??";
    }
}
