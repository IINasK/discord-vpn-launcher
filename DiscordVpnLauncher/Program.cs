namespace DiscordVpnLauncher;

/// <summary>
/// Entrypoint. O mesmo binario tem dois modos, escolhidos pelos argumentos:
///
///   (sem args)   modo orquestrador - pai NAO elevado, sem UAC
///   --broker ... modo broker       - filho elevado, um unico UAC por sessao
///
/// Ver Orchestrator e VpnBroker para o porque dessa divisao.
/// </summary>
internal static class Program
{
    public const string BrokerFlag = "--broker";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--help" or "-h" or "/?")
        {
            MostrarAjuda();
            return 0;
        }

        if (args.Length > 0 && args[0].Equals(BrokerFlag, StringComparison.OrdinalIgnoreCase))
            return VpnBroker.Run(args);

        Console.Title = "Discord VPN Launcher";
        return await Orchestrator.RunAsync().ConfigureAwait(false);
    }

    private static void MostrarAjuda()
    {
        Console.WriteLine("""
            Discord VPN Launcher

              DiscordVpnLauncher.exe
                  Sobe uma VPN gratuita fora do Brasil, abre o Discord por baixo dela
                  e derruba a VPN assim que o Discord termina de inicializar.

              DiscordVpnLauncher.exe --broker <workDir> <parentPid> <binDir>
                  Uso interno: instancia elevada que gerencia o openvpn.exe.
                  Nao chame isso na mao.

            Variavel de ambiente opcional:
              DISCORD_VPN_LAUNCHER_DISCORD
                  Caminho do Update.exe (ou Discord.exe) quando a instalacao nao esta
                  em %LocalAppData%\Discord.

            Pre-requisito: desative o "Abrir o Discord" / inicio automatico do proprio
            Discord, senao ele sobe com o IP real antes do launcher.
            """);
    }
}
