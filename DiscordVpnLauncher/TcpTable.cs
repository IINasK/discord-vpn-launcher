using System.Net;
using System.Runtime.InteropServices;

namespace DiscordVpnLauncher;

/// <summary>
/// Leitura da tabela TCP do Windows (GetExtendedTcpTable de iphlpapi.dll).
///
/// Existe para responder uma pergunta que nenhuma API gerenciada responde: por
/// QUAL interface um processo especifico esta falando. O endereco local de cada
/// conexao entrega isso - se ele for o IP do adaptador do tunel, aquele socket
/// esta saindo pela VPN.
///
/// E o que separa "o Discord abriu" de "o Discord ja conversou com os servidores
/// dele por baixo da VPN", que e a unica coisa que este launcher precisa garantir
/// antes de derrubar o tunel. Nao exige elevacao: a tabela e legivel por qualquer
/// processo (o PID dono vem junto).
/// </summary>
internal static class TcpTable
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint MIB_TCP_STATE_ESTAB = 5;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>
    /// MIB_TCPROW_OWNER_PID. Enderecos e portas vem como DWORD em ordem de rede -
    /// para o endereco isso e exatamente o que IPAddress(long) espera, entao nao ha
    /// conversao a fazer. As portas nao interessam aqui.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint Estado;
        public uint EnderecoLocal;
        public uint PortaLocal;
        public uint EnderecoRemoto;
        public uint PortaRemota;
        public uint Pid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tabela, ref int tamanho, bool ordenar, int familia, int classe, int reservado);

    /// <summary>
    /// Conexoes IPv4 no estado ESTABLISHED, com o PID dono e o endereco local.
    /// Devolve lista vazia em qualquer erro - isto e um sinal de prontidao, nunca
    /// motivo para abortar a sessao.
    /// </summary>
    public static IReadOnlyList<(int Pid, IPAddress Local)> Estabelecidas()
    {
        var tamanho = 0;

        // A tabela muda entre medir e ler, entao o buffer pode ficar pequeno no meio
        // do caminho; algumas voltas resolvem.
        for (var tentativa = 0; tentativa < 3; tentativa++)
        {
            var codigo = GetExtendedTcpTable(
                IntPtr.Zero, ref tamanho, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

            if (codigo != ERROR_INSUFFICIENT_BUFFER && codigo != 0)
                return Array.Empty<(int, IPAddress)>();

            if (tamanho <= 0)
                return Array.Empty<(int, IPAddress)>();

            var buffer = Marshal.AllocHGlobal(tamanho);
            try
            {
                codigo = GetExtendedTcpTable(
                    buffer, ref tamanho, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

                if (codigo == ERROR_INSUFFICIENT_BUFFER)
                    continue; // cresceu entre as duas chamadas

                if (codigo != 0)
                    return Array.Empty<(int, IPAddress)>();

                return Ler(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return Array.Empty<(int, IPAddress)>();
    }

    private static List<(int Pid, IPAddress Local)> Ler(IntPtr buffer)
    {
        // MIB_TCPTABLE_OWNER_PID: contagem em DWORD, seguida do vetor de linhas.
        var total = Marshal.ReadInt32(buffer);
        var tamanhoLinha = Marshal.SizeOf<MibTcpRowOwnerPid>();
        var primeira = buffer + sizeof(int);
        var resultado = new List<(int, IPAddress)>(total);

        for (var i = 0; i < total; i++)
        {
            var linha = Marshal.PtrToStructure<MibTcpRowOwnerPid>(primeira + (i * tamanhoLinha));

            if (linha.Estado == MIB_TCP_STATE_ESTAB)
                resultado.Add(((int)linha.Pid, new IPAddress(linha.EnderecoLocal)));
        }

        return resultado;
    }
}
