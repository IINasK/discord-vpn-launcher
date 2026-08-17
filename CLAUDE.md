# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Estado atual

O repositório contém **apenas o documento de projeto** [plano-discord-vpn-launcher.md](plano-discord-vpn-launcher.md). Nenhum código foi escrito ainda, e não há repositório git inicializado. O plano é a fonte de verdade: leia-o antes de implementar, e mantenha-o atualizado quando uma decisão de design mudar.

A ordem de implementação sugerida está na seção 14 do plano (checklist).

## O que o produto faz

Um único `.exe` (C#, console) que o usuário abre manualmente:

1. Sobe uma VPN gratuita (VPNGate + OpenVPN) em um relay **fora do Brasil**.
2. Lança o Discord por baixo dessa VPN, para o Discord registrar o IP não-brasileiro na inicialização.
3. **Derruba a VPN** assim que o Discord confirma que subiu.

A premissa central é que o Discord "fotografa" o IP uma única vez na inicialização — todo o valor da ferramenta depende disso.

## Arquitetura: um binário, dois modos

O ponto não-óbvio do design. `Program.cs` roteia por args:

- **Orquestrador** (pai, integridade média, **sem UAC**): baixa/filtra a lista VPNGate, escreve os `.ovpn`, mata e relança o Discord, checa o IP, exibe o popup de falha.
- **Broker** (`--broker <workDir> <parentPid>`, filho elevado, **um único UAC**): sobe o `openvpn.exe`, monitora o log, faz **retry entre candidatos** e o teardown do túnel.

**Por que o broker existe:** um processo não-elevado não consegue matar um processo elevado (Windows nega por diferença de nível de integridade). Se o filho elevado fosse o próprio `openvpn.exe`, o pai não conseguiria derrubá-lo e o cleanup quebraria. O broker elevado é dono do processo do OpenVPN, então ele consegue matá-lo e restaurar as rotas.

Consequências de design que devem ser preservadas em qualquer refatoração:

- **Todo o retry de relay fica dentro do broker.** É o que garante 1 UAC só por sessão. Por isso o pai entrega uma *lista* de candidatos (~5 melhores `!= BR`), não um config único.
- **O pai nunca mata o OpenVPN.** Ele escreve `stop.signal`; o broker faz a limpeza.
- **O Discord é lançado pelo pai não-elevado**, para herdar integridade média e rodar limpo (drag-and-drop funcionando é o teste prático disso).

## IPC via arquivos, não stdout

`runas` exige `UseShellExecute = true`, o que **impede** redirecionar o stdout do processo elevado. Logo, toda a comunicação pai↔broker passa por arquivos em `%LocalAppData%\DiscordVpnLauncher\work\`:

| Arquivo | Escrito por | Lido por |
|---|---|---|
| `candN.ovpn` | pai | broker |
| `openvpn.log` (via `--log`) | openvpn | broker |
| `vpn-status.txt` (`connected:<país>` / `failed:all`) | broker | pai |
| `stop.signal` | pai | broker |

Binários extraídos ficam em `%LocalAppData%\DiscordVpnLauncher\bin\`.

## Sinais concretos, nunca `sleep` fixo

| Evento | Sinal | Timeout |
|---|---|---|
| VPN subiu | `Initialization Sequence Completed` no log do OpenVPN | 20 s por candidato |
| Está fora do BR | `GET https://ipinfo.io/country` retorna `!= BR` | 5 s |
| Discord pronto | pipe `\\.\pipe\discord-ipc-0` existe | 30 s |
| Pai morreu (visto pelo broker) | `Process.GetProcessById(parentPid)` falha | poll 1 s |

## Invariantes que não podem ser quebrados

- **O túnel nunca fica aberto.** Dois mecanismos redundantes: `stop.signal` no `finally` do pai **e** watchdog do broker sobre o PID do pai.
- **Um UAC por execução** (no broker), zero UAC no Discord.
- **Matar todos os `Discord.exe` antes de relançar** — ele roda em vários processos, e sem o kill o relaunch só foca a janela existente, sem re-captura de IP.
- **Mutex nomeado** para instância única do launcher (duas execuções concorrentes brigariam pela mesma `workDir` e pelas rotas).
- **Timeout do Discord não travar o processo:** se o pipe nunca aparecer, derrubar a VPN e encerrar de qualquer forma.

## Recursos embutidos (OpenVPN)

`openvpn.exe` e `wintun.dll` (de uma instalação oficial do **OpenVPN 2.6+**) vão como `EmbeddedResource` e são extraídos para `bin\` no 1º uso — sem instalador, sem download externo. O 2.6 usa wintun por padrão, evitando o driver TAP clássico; ainda assim, passe `--windows-driver wintun` explícito. `wintun.dll` precisa estar na mesma pasta do `openvpn.exe`.

O bundle elimina a *instalação de driver*, não a necessidade de elevação para *usar* o adaptador — por isso só o broker invoca o `openvpn.exe`.

## Build

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true
```

Saída: `.exe` único em `bin\Release\net8.0-windows\win-x64\publish\` (~60-70 MB — o runtime .NET embutido é custo aceito). Target `net8.0-windows` por causa das APIs Win-only.

Não há suíte de testes automatizados. A validação é a bateria manual da **seção 12 do plano** (extração dos binários, contagem de UACs, Discord não-elevado via drag-and-drop, país via ipinfo, cleanup após kill forçado do pai, caminho de falha com os dois botões do popup).

## Detalhes de implementação fáceis de errar

- **Parser do CSV do VPNGate** (`https://www.vpngate.net/api/iphone/`, sem login/chave): tolerante a mudanças — resolva colunas por nome de cabeçalho, pule linhas de comentário `*`. O `.ovpn` completo vem em base64 na coluna `OpenVPN_ConfigData_Base64`, com CA/cert/key inline.
- **Popup de falha:** `MessageBox` via P/Invoke de `user32.dll` (evita arrastar `System.Windows.Forms` para um console app). Botões: **Fechar** e **Continuar sem VPN** (abre o Discord no IP real). Retorno `6` = continuar, `7` = fechar. Dispara em: sem rede, lista `!= BR` vazia, `failed:all`, timeout da VPN, ou ipinfo retornando BR.
- **Lançar o Discord** pelo stub do Squirrel: `%LocalAppData%\Discord\Update.exe --processStart Discord.exe` (aponta sempre para a versão atual). Assume instalação padrão — instalação fora de `%LocalAppData%\Discord` precisa de fallback.
- **Pré-requisito manual do usuário** (documentar no README): desativar o auto-start do Discord. Se ficar ligado, o Discord abre pelo IP real antes do launcher e mata todo o propósito. O launcher não mexe nisso automaticamente.
- Um exe self-extracting que solta `openvpn.exe` e mexe em rede tende a acionar **SmartScreen/antivírus**. Aceito para uso próprio.
