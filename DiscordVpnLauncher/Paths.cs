using System.Reflection;

namespace DiscordVpnLauncher;

/// <summary>
/// Localizacao de tudo em disco e extracao dos binarios embutidos.
///
/// A pasta raiz fica em %LocalAppData%\DiscordVpnLauncher com duas subpastas:
///   bin\   openvpn.exe + wintun.dll extraidos dos EmbeddedResource no 1o uso
///   work\  arquivos efemeros da sessao (candidatos, log, status, sinal de stop)
///
/// work\ e o canal de IPC entre pai e broker: como "runas" exige
/// UseShellExecute = true, nao ha como redirecionar stdout do processo elevado,
/// entao a comunicacao passa por arquivos.
/// </summary>
internal sealed class Paths
{
    public string Root { get; }
    public string BinDir { get; }
    public string WorkDir { get; }

    public string OpenVpnExe => Path.Combine(BinDir, "openvpn.exe");
    public string WintunDll => Path.Combine(BinDir, "wintun.dll");

    /// <summary>
    /// Utilitario que cria/remove o adaptador de rede. O openvpn.exe NAO cria o
    /// adaptador wintun sozinho: sem um adaptador pronto ele aborta com "There are
    /// no TAP-Windows, Wintun or ovpn-dco adapters on this system".
    /// </summary>
    public string TapCtlExe => Path.Combine(BinDir, "tapctl.exe");

    public string VpnStatusFile => Path.Combine(WorkDir, "vpn-status.txt");
    public string StopSignalFile => Path.Combine(WorkDir, "stop.signal");
    public string OpenVpnLog => Path.Combine(WorkDir, "openvpn.log");
    public string BrokerLog => Path.Combine(WorkDir, "broker.log");

    private Paths(string root, string binDir, string workDir)
    {
        Root = root;
        BinDir = binDir;
        WorkDir = workDir;
    }

    /// <summary>Layout padrao, usado pelo orquestrador.</summary>
    public static Paths Default()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiscordVpnLauncher");
        return new Paths(root, Path.Combine(root, "bin"), Path.Combine(root, "work"));
    }

    /// <summary>
    /// Layout recebido por argumento, usado pelo broker. O pai passa os caminhos
    /// explicitamente porque o UAC pode ser satisfeito com credenciais de OUTRA
    /// conta de administrador - nesse caso o %LocalAppData% do broker seria
    /// diferente do do pai, e recalcular os caminhos apontaria para o lugar errado.
    /// </summary>
    public static Paths FromWorkDir(string workDir, string binDir)
    {
        var root = Path.GetDirectoryName(workDir.TrimEnd(Path.DirectorySeparatorChar)) ?? workDir;
        return new Paths(root, binDir, workDir);
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(BinDir);
        Directory.CreateDirectory(WorkDir);
    }

    public string CandidateConfig(int index) => Path.Combine(WorkDir, $"cand{index}.ovpn");

    /// <summary>Candidatos em ordem numerica (cand1, cand2, ... e nao cand1, cand10, cand2).</summary>
    public IReadOnlyList<string> EnumerateCandidates()
    {
        if (!Directory.Exists(WorkDir))
            return Array.Empty<string>();

        return Directory.GetFiles(WorkDir, "cand*.ovpn")
            .Select(path => (path, order: ParseCandidateIndex(path)))
            .Where(t => t.order >= 0)
            .OrderBy(t => t.order)
            .Select(t => t.path)
            .ToList();
    }

    private static int ParseCandidateIndex(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return int.TryParse(name.AsSpan("cand".Length), out var index) ? index : -1;
    }

    /// <summary>
    /// Remove restos de execucoes anteriores. Nao mexe em bin\ - os binarios
    /// extraidos sao reaproveitados entre sessoes.
    /// </summary>
    public void CleanWorkDir()
    {
        if (!Directory.Exists(WorkDir))
            return;

        foreach (var file in Directory.GetFiles(WorkDir))
            TryDelete(file);
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // arquivo em uso: nao e fatal, a proxima sessao sobrescreve
        }
        catch (UnauthorizedAccessException)
        {
            // escrito pelo broker elevado; o pai nao-elevado pode nao conseguir apagar
        }
    }

    /// <summary>Prefixo dos recursos embutidos vindos da pasta Resources\ do projeto.</summary>
    private const string ResourcePrefix = "DiscordVpnLauncher.Resources.";

    /// <summary>
    /// Extrai TODO recurso embutido de Resources\ para bin\, se faltar ou se o tamanho
    /// divergir (caso de upgrade do launcher).
    ///
    /// Sao mais arquivos do que openvpn.exe + wintun.dll: o openvpn.exe depende das
    /// DLLs do OpenSSL e sai com 0xC0000135 sem elas. Todas tem que cair na MESMA
    /// pasta, que e tambem o WorkingDirectory usado ao inicia-lo.
    /// </summary>
    /// <returns>null em caso de sucesso, ou a mensagem de erro.</returns>
    public string? ExtractEmbeddedBinaries()
    {
        EnsureDirectories();

        var assembly = Assembly.GetExecutingAssembly();
        var recursos = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (recursos.Count == 0)
        {
            return "Este build nao tem os binarios do OpenVPN embutidos. " +
                   "Rode tools\\get-openvpn-binaries.ps1 e recompile.";
        }

        foreach (var recurso in recursos)
        {
            var nomeArquivo = recurso[ResourcePrefix.Length..];
            var error = ExtractOne(assembly, recurso, Path.Combine(BinDir, nomeArquivo));
            if (error is not null)
                return error;
        }

        // O broker depende destes tres por nome; os demais sao dependencias deles.
        var faltando = new[] { OpenVpnExe, WintunDll, TapCtlExe }
            .Where(p => !File.Exists(p))
            .Select(Path.GetFileName)
            .ToList();

        if (faltando.Count > 0)
        {
            return $"Faltam binarios embutidos neste build: {string.Join(", ", faltando)}. " +
                   "Rode tools\\get-openvpn-binaries.ps1 e recompile.";
        }

        return null;
    }

    private static string? ExtractOne(Assembly assembly, string resourceName, string targetPath)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return $"Nao foi possivel abrir o recurso embutido '{resourceName}'.";

        if (File.Exists(targetPath) && new FileInfo(targetPath).Length == stream.Length)
            return null; // ja extraido nesta versao

        try
        {
            // Grava em arquivo temporario e move: evita deixar um binario truncado
            // se a extracao for interrompida no meio.
            var temp = targetPath + ".tmp";
            using (var output = File.Create(temp))
                stream.CopyTo(output);
            File.Move(temp, targetPath, overwrite: true);
            return null;
        }
        catch (IOException ex)
        {
            // Tipico: openvpn.exe ainda rodando de uma sessao anterior.
            return File.Exists(targetPath)
                ? null // versao antiga serve; segue o jogo
                : $"Falha ao extrair '{Path.GetFileName(targetPath)}': {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Sem permissao para escrever em '{targetPath}': {ex.Message}";
        }
    }
}
