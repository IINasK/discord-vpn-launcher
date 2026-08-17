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

    private static void Write(string prefix, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {prefix} {message}";

        lock (Gate)
        {
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
