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
        // O console do Windows abre em codepage OEM (850/437 no Brasil): sem esta
        // troca, todo acento nas mensagens sai como caractere quebrado. Ignora falha
        // porque a saida pode estar redirecionada para algo que nao aceita a troca -
        // e nesse caso o texto continua legivel, so sem acento.
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
        }

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

        Console.WriteLine("Discord VPN Launcher - diagnóstico");
        Console.WriteLine();
        Console.WriteLine($"  Launcher      : {Environment.ProcessPath}");
        Console.WriteLine($"  Elevado       : {(ElevationHelper.IsElevated() ? "SIM (não deveria)" : "não (correto)")}");
        Console.WriteLine($"  Discord em    : {DiscordController.DescreverAlvo()}");
        Console.WriteLine($"  Binários em   : {paths.BinDir}");
        Console.WriteLine($"  openvpn.exe   : {(File.Exists(paths.OpenVpnExe) ? "extraído" : "ainda não extraído")}");
        Console.WriteLine($"  wintun.dll    : {(File.Exists(paths.WintunDll) ? "extraído" : "ainda não extraído")}");
        Console.WriteLine($"  Logs em       : {paths.WorkDir}");
        Console.WriteLine();
        Console.WriteLine("  Qualquer item incorreto acima é o ponto de partida da investigação.");
    }

    private static void MostrarAjuda()
    {
        Console.WriteLine("""
            Discord VPN Launcher

              Discord.exe
                  Conecta-se a uma VPN gratuita fora do Brasil, inicia o Discord por baixo
                  dela e encerra a VPN assim que o Discord conclui o registro do IP.

              Discord.exe --diagnostico
                  Exibe onde o Discord foi localizado e o estado da instalação, sem
                  iniciar nada. Solicite este comando primeiro ao diagnosticar problemas.

              Discord.exe --broker <workDir> <parentPid> <binDir>
                  Uso interno: instância elevada que gerencia o openvpn.exe.
                  Não deve ser executado manualmente.

            Variáveis de ambiente opcionais:
              DISCORD_VPN_LAUNCHER_DISCORD
                  Caminho do Update.exe (ou Discord.exe) quando a instalação não está
                  em %LocalAppData%\Discord.

              DISCORD_VPN_LAUNCHER_ESPERA
                  Segundos de VPN mantidos após o Discord iniciar a comunicação pelo
                  túnel (padrão 5). Aumente caso o IP registrado ainda esteja saindo
                  como brasileiro.

              DISCORD_VPN_LAUNCHER_ESTABILIZACAO
                  Segundos de espera entre conectar a VPN e checar a estabilidade do
                  túnel (padrão 0). A checagem em si acontece de qualquer forma; esta
                  variável só acrescenta um respiro antes dela.

              DISCORD_VPN_LAUNCHER_TETO_MANUAL
                  Segundos que o aviso "clique em OK para desligar a VPN" aguarda
                  antes de desligar sozinho (padrão 600). Use 0 para desligar a VPN
                  imediatamente, sem aviso.

            Pré-requisito: desative a opção "Abrir o Discord" (inicialização automática
            do próprio Discord), caso contrário ele será iniciado com o IP real.
            """);
    }
}
