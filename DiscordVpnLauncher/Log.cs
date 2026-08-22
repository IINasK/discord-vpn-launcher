namespace DiscordVpnLauncher;

/// <summary>
/// Log simples de console, com espelho opcional em arquivo.
///
/// O broker roda com a janela escondida (ver ElevationHelper), portanto o console
/// dele nao e visivel: e por isso que ele espelha tudo em work\broker.log.
/// </summary>
internal static class Log
{
    private static string? _mirrorFile;
    private static readonly object Gate = new();

    public static void MirrorTo(string path)
    {
        lock (Gate)
            _mirrorFile = path;
    }

    public static void Info(string message) => Write("  ", message);

    public static void Step(string message) => Write("->", message);

    public static void Warn(string message) => Write("!!", message);

    public static void Error(string message) => Write("XX", message);

    /// <summary>
    /// Vai só para o arquivo, nunca para o console.
    ///
    /// Existe por causa de um caso concreto: as tentativas de consulta de IP que
    /// falham logo depois de o túnel subir são normais (rotas e DNS acabaram de ser
    /// trocados) e se resolvem sozinhas na tentativa seguinte, mas usuários da 1.3
    /// leram aquele "Este host não é conhecido" na tela como erro e reportaram. O
    /// texto continua no `launcher.log`, que é onde ele serve para diagnóstico —
    /// tirar do console não é esconder problema, é não dar alarme falso.
    ///
    /// Só para detalhe que se resolve sozinho: o que exige ação do usuário, ou o que
    /// explica uma falha de verdade, continua em Warn/Error.
    /// </summary>
    public static void Diag(string message) => Write("  ", message, apenasArquivo: true);

    private static void Write(string prefix, string message, bool apenasArquivo = false)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {prefix} {message}";

        lock (Gate)
        {
            if (!apenasArquivo)
                Console.WriteLine(line);

            if (_mirrorFile is null)
                return;

            try
            {
                File.AppendAllText(_mirrorFile, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // log e diagnostico, nunca motivo para abortar o fluxo
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
