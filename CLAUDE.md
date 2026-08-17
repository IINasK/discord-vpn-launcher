# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Estado atual

O projeto está implementado em [DiscordVpnLauncher/](DiscordVpnLauncher/) conforme [plano-discord-vpn-launcher.md](plano-discord-vpn-launcher.md), que continua sendo a fonte de verdade do desenho — mantenha-o atualizado quando uma decisão mudar.

Não há suíte de testes automatizados. A validação é a bateria manual da **seção 12 do plano** (extração dos binários, contagem de UACs, Discord não-elevado via drag-and-drop, país via ipinfo, cleanup após kill forçado do pai, caminho de falha com os dois botões do popup).

## O que o produto faz

Um único `.exe` (C#, console) que o usuário abre manualmente:

1. Sobe uma VPN gratuita (VPNGate + OpenVPN) em um relay **fora do Brasil**.
2. Lança o Discord por baixo dessa VPN, para o Discord registrar o IP não-brasileiro na inicialização.
3. **Derruba a VPN** assim que o Discord confirma que subiu.

A premissa central é que o Discord "fotografa" o IP uma única vez na inicialização — todo o valor da ferramenta depende disso.

## Arquitetura: um binário, dois modos

O ponto não-óbvio do design. `Program.cs` roteia por args:

- **Orquestrador** (pai, integridade média, **sem UAC**): baixa/filtra a lista VPNGate, escreve os `.ovpn`, mata e relança o Discord, checa o IP, exibe o popup de falha.
- **Broker** (`--broker <workDir> <parentPid> <binDir>`, filho elevado, **um único UAC**): sobe o `openvpn.exe`, monitora o log, faz **retry entre candidatos** e o teardown do túnel.

O `binDir`/`workDir` vão como argumento em vez de serem recalculados pelo broker porque o UAC pode ser satisfeito com credenciais de **outra** conta de administrador — nesse caso o `%LocalAppData%` do broker não é o do pai.

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
| `vpn-status.txt` (`starting` / `trying:N` / `connected:<país>` / `failed:*`) | broker | pai |
| `stop.signal` | pai | broker |
| `broker.log` | broker | humano |

O broker roda com a janela **oculta** (`WindowStyle.Hidden`), então o console dele não é visível — todo o diagnóstico dele vai para `broker.log` via [Log.cs](DiscordVpnLauncher/Log.cs). Ao investigar um problema no túnel, é esse arquivo que interessa.

Binários extraídos ficam em `%LocalAppData%\DiscordVpnLauncher\bin\`.

O país do candidato viaja como comentário `# vpngate-country=XX` no topo do próprio `.ovpn` (o OpenVPN ignora linhas `#`), evitando um segundo canal de metadados.

## Sinais concretos, nunca `sleep` fixo

| Evento | Sinal | Timeout |
|---|---|---|
| VPN subiu | `Initialization Sequence Completed` no log do OpenVPN | 20 s por candidato |
| Está fora do BR | `GET https://ipinfo.io/country` retorna `!= BR` | 5 s |
| Discord pronto | pipe `\\.\pipe\discord-ipc-0` existe | 30 s |
| Pai morreu (visto pelo broker) | `Process.GetProcessById(parentPid)` falha | poll 1 s |

A espera do pai pelo broker usa timeout **por inatividade** (45 s sem mudança de status), não por total: criar o adaptador na primeira vez instala driver, e o pior caso legítimo — isso mais 5 candidatos de 20 s — estoura qualquer prazo fixo curto. O broker publica `starting`/`trying:N` justamente para renovar esse prazo. Teto absoluto de 3 min.

## Invariantes que não podem ser quebrados

- **`app.manifest` tem que continuar `asInvoker`.** É o que mantém o pai em integridade média para o Discord herdar. Um `requireAdministrator` ali quebraria silenciosamente o objetivo inteiro — o Discord passaria a rodar como admin.
- **O túnel nunca fica aberto.** Dois mecanismos redundantes: `stop.signal` no `finally` do pai **e** watchdog do broker sobre o PID do pai.
- **Um UAC por execução** (no broker), zero UAC no Discord.
- **Matar todos os `Discord.exe` antes de relançar** — ele roda em vários processos, e sem o kill o relaunch só foca a janela existente, sem re-captura de IP.
- **Mutex nomeado** para instância única do launcher (duas execuções concorrentes brigariam pela mesma `workDir` e pelas rotas).
- **Timeout do Discord não travar o processo:** se o pipe nunca aparecer, derrubar a VPN e encerrar de qualquer forma.

## Recursos embutidos (OpenVPN)

Tudo que está em `DiscordVpnLauncher/Resources/` é embutido (`EmbeddedResource Include="Resources\*"`) e extraído para `bin\` no 1º uso — sem instalador, sem download externo em runtime. Popule a pasta com `tools/get-openvpn-binaries.ps1`, que baixa e extrai sem instalar nada e valida a assinatura Authenticode de cada arquivo.

**São 7 arquivos, não 2** — três coisas que o plano original não previa (registradas na seção 15 dele):

- `openvpn.exe` **não roda sozinho**: sem `libcrypto-3-x64.dll`, `libssl-3-x64.dll`, `libpkcs11-helper-1.dll` e `vcruntime140.dll` ele sai com `0xC0000135` (DLL not found) sem escrever nada no log — sintoma que parece "openvpn não iniciou" sem explicação.
- **O MSI do OpenVPN não contém `wintun.dll`**, nem com `ADDLOCAL=ALL`. Ele vem do upstream (wintun.net, assinado pela WireGuard LLC), que é a origem do arquivo de qualquer forma.
- **`tapctl.exe` é obrigatório** — ver a seção sobre o adaptador, abaixo.

Por isso o `.csproj` usa curinga em vez de listar arquivos: a lista de dependências muda entre versões do OpenVPN. Fique na série **2.6.x** — a 2.7 removeu mais opções legadas e não foi validada com os configs do VPNGate.

Todos os arquivos caem na **mesma pasta** (`bin\`), que também é o `WorkingDirectory` do `openvpn.exe` — é assim que `wintun.dll` e as DLLs do OpenSSL são resolvidas.

O bundle elimina a *instalação de driver*, não a necessidade de elevação para *usar* o adaptador — por isso só o broker invoca o `openvpn.exe`.

## O adaptador wintun tem que ser criado à mão

O ponto mais fácil de quebrar sem perceber. **O `openvpn.exe` não cria o adaptador**: sem um pronto ele completa o TLS, recebe o `PUSH_REPLY` e só então morre em `open_tun` com *"There are no TAP-Windows, Wintun or ovpn-dco adapters on this system"* — parece falha de relay, mas não é.

Por isso `VpnBroker.GarantirAdaptador` roda `tapctl create --hardware-id wintun --name DiscordVpnLauncher` antes dos candidatos, e o OpenVPN recebe `--dev-node DiscordVpnLauncher` (o sanitizador remove qualquer `dev-node` que venha do config, para não duplicar a opção).

- `tapctl.exe` tem manifest `requireAdministrator` — só roda elevado. Como o broker já está elevado, invocá-lo **não** gera um segundo UAC (filho de processo elevado herda o token).
- **Matar o `openvpn.exe` não remove o adaptador** — desfaz só as rotas. `VpnBroker.RemoverAdaptador` chama `tapctl delete` no `finally`; é isso que faz o teste 6 da seção 12 (adaptador sumiu do `ipconfig`) passar.
- Um adaptador com o nosso nome sobrando de uma sessão anterior é **reaproveitado**, não recriado.

**Os binários não estão no git** (`.gitignore` cobre `**/Resources/*.exe` e `*.dll`). A **compilação funciona sem eles** (só emite aviso) e a falha aparece em runtime com mensagem explícita — ao investigar "não consegue subir a VPN", verifique antes de tudo se os recursos foram embutidos.

## Build

```powershell
dotnet publish DiscordVpnLauncher -c Release
```

Self-contained, single-file e `win-x64` já estão no `.csproj` — não precisa repetir as flags na linha de comando. Saída: `DiscordVpnLauncher\bin\Release\net8.0-windows\win-x64\publish\DiscordVpnLauncher.exe` (~60-70 MB — o runtime .NET embutido é custo aceito). Target `net8.0-windows` por causa das APIs Win-only.

## Detalhes de implementação fáceis de errar

- **Parser do CSV do VPNGate** (`https://www.vpngate.net/api/iphone/`, sem login/chave): tolerante a mudanças — colunas resolvidas por nome de cabeçalho, linhas `*` puladas. O base64 é lido do **último campo** da linha, não de um índice fixo: a coluna `Message` pode conter vírgulas e deslocar tudo o que vem depois dela.
- **Os configs do VPNGate são antigos e não sobem no OpenVPN 2.6 sem tratamento.** `VpnGateClient.PrepararConfig` remove opções que a 2.6 rejeita (`ncp-disable`, `keysize`, `ns-cert-type`, `tls-remote`, …), remove `explicit-exit-notify` (fatal quando o relay usa `proto tcp`), e injeta `data-ciphers`/`data-ciphers-fallback` + `allow-compression asym` quando o config usa compressão. Sem isso o OpenVPN aborta com "Unrecognized option" e **todos** os candidatos falham — sintoma que parece falta de relay, mas não é. Também injeta `connect-retry-max 2` para o broker desistir rápido e partir para o próximo candidato.
- **Popup de falha:** `MessageBox` via P/Invoke de `user32.dll` (evita arrastar `System.Windows.Forms` para um console app). Botões: **Fechar** e **Continuar sem VPN** (abre o Discord no IP real). Retorno `6` = continuar, `7` = fechar. Dispara em: sem rede, lista `!= BR` vazia, `failed:all`, timeout da VPN, ou ipinfo retornando BR.
- **Lançar o Discord** pelo stub do Squirrel: `%LocalAppData%\Discord\Update.exe --processStart Discord.exe` (aponta sempre para a versão atual). Assume instalação padrão — instalação fora de `%LocalAppData%\Discord` precisa de fallback.
- **Pré-requisito manual do usuário** (documentar no README): desativar o auto-start do Discord. Se ficar ligado, o Discord abre pelo IP real antes do launcher e mata todo o propósito. O launcher não mexe nisso automaticamente.
- Um exe self-extracting que solta `openvpn.exe` e mexe em rede tende a acionar **SmartScreen/antivírus**. Aceito para uso próprio.
