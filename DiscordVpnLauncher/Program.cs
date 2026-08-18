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

        if (args.Length > 0 && args[0] is "--diagnostico")
        {
            MostrarDiagnostico();
            return 0;
        }

        Console.Title = "Discord VPN Launcher";
        return await Orchestrator.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Estado da instalacao, sem tocar em nada. Pensado para suporte a distancia:
    /// "roda Discord.exe --diagnostico e me manda o que aparece" responde a maior
    /// parte das perguntas sem precisar de log nenhum.
    /// </summary>
    private static void MostrarDiagnostico()
    {
        var paths = Paths.Default();

        Console.WriteLine("Discord VPN Launcher - diagnostico");
        Console.WriteLine();
        Console.WriteLine($"  Launcher      : {Environment.ProcessPath}");
        Console.WriteLine($"  Elevado       : {(ElevationHelper.IsElevated() ? "SIM (nao deveria)" : "nao (correto)")}");
        Console.WriteLine($"  Discord em    : {DiscordController.DescreverAlvo()}");
        Console.WriteLine($"  Binarios em   : {paths.BinDir}");
        Console.WriteLine($"  openvpn.exe   : {(File.Exists(paths.OpenVpnExe) ? "extraido" : "ainda nao extraido")}");
        Console.WriteLine($"  wintun.dll    : {(File.Exists(paths.WintunDll) ? "extraido" : "ainda nao extraido")}");
        Console.WriteLine($"  Logs em       : {paths.WorkDir}");
        Console.WriteLine();
        Console.WriteLine("  Se algo aqui estiver errado, e por onde comecar.");
    }

    private static void MostrarAjuda()
    {
        Console.WriteLine("""
            Discord VPN Launcher

              Discord.exe
                  Sobe uma VPN gratuita fora do Brasil, abre o Discord por baixo dela
                  e derruba a VPN assim que o Discord termina de se registrar.

              Discord.exe --diagnostico
                  Mostra onde o Discord foi encontrado e o estado da instalacao,
                  sem abrir nada. Peca isto primeiro quando alguem relatar problema.

              Discord.exe --broker <workDir> <parentPid> <binDir>
                  Uso interno: instancia elevada que gerencia o openvpn.exe.
                  Nao chame isso na mao.

            Variaveis de ambiente opcionais:
              DISCORD_VPN_LAUNCHER_DISCORD
                  Caminho do Update.exe (ou Discord.exe) quando a instalacao nao esta
                  em %LocalAppData%\Discord.

              DISCORD_VPN_LAUNCHER_ESPERA
                  Segundos de VPN mantidos depois que o Discord comeca a falar pelo
                  tunel (padrao 30, maximo 300). Aumente se o IP registrado ainda
                  sair como brasileiro.

            Pre-requisito: desative o "Abrir o Discord" / inicio automatico do proprio
            Discord, senao ele sobe com o IP real antes do launcher.
            """);
    }
}
