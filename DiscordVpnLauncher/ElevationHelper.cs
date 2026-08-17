using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace DiscordVpnLauncher;

internal static class ElevationHelper
{
    /// <summary>Codigo de erro do Win32 quando o usuario cancela o prompt de UAC.</summary>
    private const int ERROR_CANCELLED = 1223;

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Relanca este mesmo executavel em modo broker, elevado. Este e o UNICO ponto
    /// do programa que dispara UAC - por isso todo o retry de relay mora dentro do
    /// broker, e nao aqui.
    /// </summary>
    /// <returns>O processo do broker, ou null se o usuario recusou o UAC.</returns>
    public static Process? LaunchBrokerElevated(Paths paths, int parentPid)
    {
        // Environment.ProcessPath aponta para o .exe real, inclusive em publish
        // single-file (Assembly.Location vem vazio nesse cenario).
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Nao foi possivel determinar o caminho do executavel.");

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true, // obrigatorio para o verbo runas
            Verb = "runas",
            // Sem janela: o broker nao tem nada a dizer ao usuario e o console dele
            // apareceria como uma segunda janela solta. O diagnostico vai para broker.log.
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add(Program.BrokerFlag);
        startInfo.ArgumentList.Add(paths.WorkDir);
        startInfo.ArgumentList.Add(parentPid.ToString());
        startInfo.ArgumentList.Add(paths.BinDir);

        try
        {
            return Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            Log.Warn("UAC recusado pelo usuario.");
            return null;
        }
    }
}
