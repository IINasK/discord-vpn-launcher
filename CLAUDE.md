# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Estado atual

O projeto está implementado em [DiscordVpnLauncher/](DiscordVpnLauncher/) conforme [plano-discord-vpn-launcher.md](plano-discord-vpn-launcher.md), que continua sendo a fonte de verdade do desenho — mantenha-o atualizado quando uma decisão mudar.

Não há suíte de testes automatizados. A validação é a bateria manual da **seção 12 do plano** (extração dos binários, contagem de UACs, Discord não-elevado via drag-and-drop, país via ipinfo, cleanup após kill forçado do pai, caminho de falha com os dois botões do popup).

## O que o produto faz

Um único `.exe` (C#, console) que o usuário abre manualmente:

1. Sobe uma VPN gratuita (VPNGate + OpenVPN) em um relay **fora do Brasil**.
2. Lança o Discord por baixo dessa VPN, para o Discord registrar o IP não-brasileiro na inicialização.
3. **Derruba a VPN** assim que o Discord terminou de se registrar.

## A sacada: o IP é fotografado uma vez e a reconexão não o substitui

É a premissa central, e ela foi **confirmada na prática** — não é teoria de desenho.

O Discord registra o IP no login inicial. Quando o launcher derruba o túnel, a conexão do Discord cai junto (o IP de origem some do adaptador) e ele **reconecta pelo IP real brasileiro** — e continua funcionando normalmente, com o IP não-brasileiro que já havia sido registrado. A reconexão não refaz esse registro.

Isso não é um efeito colateral tolerado: é o que dá razão à ferramenta inteira. Se a reconexão sobrescrevesse o registro, seria preciso manter a VPN de pé o tempo todo, e não haveria launcher nenhum — haveria só uma VPN. É por isso que o produto é um `.exe` que abre, faz o serviço e sai de cena, deixando o usuário com a rede normal, latência normal e sem VPN rodando.

Duas consequências que qualquer refatoração precisa preservar:

- **A janela de VPN só precisa cobrir o login**, não a sessão. Curta de propósito.
- **Mas precisa cobrir o login inteiro.** Derrubar o túnel cedo demais é a única forma de perder tudo — o Discord loga pelo IP real e não há segunda chance sem matar e relançar. Ver a seção sobre a captura de IP, abaixo.

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
| `launcher.log` | pai | humano |

O broker roda com a janela **oculta** (`WindowStyle.Hidden`), então o console dele não é visível — todo o diagnóstico dele vai para `broker.log` via [Log.cs](DiscordVpnLauncher/Log.cs). Ao investigar um problema no túnel, é esse arquivo que interessa; para uma falha depois do túnel subir (país, Discord), é o `launcher.log`, espelho do console do pai. Os dois são apagados no início de cada sessão, junto com o resto da `work\` — o `launcher.log` só começa a ser espelhado **depois** dessa limpeza.

Binários extraídos ficam em `%LocalAppData%\DiscordVpnLauncher\bin\`.

O país do candidato viaja como comentário `# vpngate-country=XX` no topo do próprio `.ovpn` (o OpenVPN ignora linhas `#`), evitando um segundo canal de metadados.

## Sinais concretos, nunca `sleep` fixo

| Evento | Sinal | Timeout |
|---|---|---|
| VPN subiu | `Initialization Sequence Completed` no log do OpenVPN | 20 s por candidato |
| Está fora do BR | `GET .../country` retorna `!= BR` | 5 s por requisição, janela de 20 s |
| Processo do Discord subiu | pipe `\\.\pipe\discord-ipc-0` existe | 30 s |
| Discord capturou o IP | conexão ESTABLISHED do Discord com origem no IP do túnel, **mais** folga fixa | 60 s + 30 s |
| Pai morreu (visto pelo broker) | `Process.GetProcessById(parentPid)` falha | poll 1 s |

A espera do pai pelo broker usa timeout **por inatividade** (45 s sem mudança de status), não por total: criar o adaptador na primeira vez instala driver, e o pior caso legítimo — isso mais 5 candidatos de 20 s — estoura qualquer prazo fixo curto. O broker publica `starting`/`trying:N` justamente para renovar esse prazo. Teto absoluto de 3 min.

## Invariantes que não podem ser quebrados

- **`app.manifest` tem que continuar `asInvoker`.** É o que mantém o pai em integridade média para o Discord herdar. Um `requireAdministrator` ali quebraria silenciosamente o objetivo inteiro — o Discord passaria a rodar como admin.
- **O túnel nunca fica aberto.** Dois mecanismos redundantes: `stop.signal` no `finally` do pai **e** watchdog do broker sobre o PID do pai.
- **Um UAC por execução** (no broker), zero UAC no Discord.
- **Matar todos os `Discord.exe` antes de relançar** — ele roda em vários processos, e sem o kill o relaunch só foca a janela existente, sem re-captura de IP.
- **Mutex nomeado** para instância única do launcher (duas execuções concorrentes brigariam pela mesma `workDir` e pelas rotas).
- **Timeout do Discord não travar o processo:** se o pipe nunca aparecer, derrubar a VPN e encerrar de qualquer forma.
- **A VPN só cai depois de o Discord ter falado pelo túnel.** O pipe de IPC sobe junto com o processo, ~5 s antes de o app tocar no gateway — derrubar o túnel ali fazia o Discord registrar o IP real com a VPN tendo funcionado do início ao fim. `Orchestrator.EsperarCapturaDeIp` espera uma conexão ESTABLISHED do Discord com origem no IP do adaptador ([TcpTable.cs](DiscordVpnLauncher/TcpTable.cs)) e só então segura mais 30 s (`DISCORD_VPN_LAUNCHER_ESPERA`) para o login concluir. É o que imita o teste manual que comprovadamente funciona: Proton conectado, Discord aberto, esperar carregar, desconectar. **A queda da conexão no teardown é esperada e inofensiva** — ver a seção da sacada, no topo.

## Recursos embutidos (OpenVPN)

Tudo que está em `DiscordVpnLauncher/Resources/` é embutido (`EmbeddedResource Include="Resources\*"`) e extraído para `bin\` no 1º uso — sem instalador, sem download externo em runtime. Popule a pasta com `tools/get-openvpn-binaries.ps1`, que baixa e extrai sem instalar nada e valida a assinatura Authenticode de cada arquivo.

**São 6 arquivos, não 2** — três coisas que o plano original não previa (registradas na seção 15 dele):

- `openvpn.exe` **não roda sozinho**: sem `libcrypto-3-x64.dll`, `libssl-3-x64.dll`, `libpkcs11-helper-1.dll` e `vcruntime140.dll` ele sai com `0xC0000135` (DLL not found) sem escrever nada no log — sintoma que parece "openvpn não iniciou" sem explicação.
- **O MSI do OpenVPN não contém `wintun.dll`**, nem com `ADDLOCAL=ALL`. Ele vem do upstream (wintun.net, assinado pela WireGuard LLC), que é a origem do arquivo de qualquer forma.
- **`tapctl.exe` saiu do bundle** — quem cria o adaptador é o `wintun.dll` via P/Invoke; ver a seção sobre o adaptador, abaixo.

Por isso o `.csproj` usa curinga em vez de listar arquivos: a lista de dependências muda entre versões do OpenVPN. Fique na série **2.6.x** — a 2.7 removeu mais opções legadas e não foi validada com os configs do VPNGate.

Todos os arquivos caem na **mesma pasta** (`bin\`), que também é o `WorkingDirectory` do `openvpn.exe` — é assim que `wintun.dll` e as DLLs do OpenSSL são resolvidas.

O bundle elimina a *instalação de driver*, não a necessidade de elevação para *usar* o adaptador — por isso só o broker invoca o `openvpn.exe`.

## O adaptador wintun tem que ser criado à mão — pelo `wintun.dll`, não pelo `tapctl`

O ponto mais fácil de quebrar sem perceber. **O `openvpn.exe` não cria o adaptador**: sem um pronto ele completa o TLS, recebe o `PUSH_REPLY` e só então morre em `open_tun` com *"There are no TAP-Windows, Wintun or ovpn-dco adapters on this system"* — parece falha de relay, mas não é.

Quem cria é [WintunAdapter.cs](DiscordVpnLauncher/WintunAdapter.cs), por P/Invoke em `WintunCreateAdapter`, antes de o broker tentar os candidatos; o OpenVPN então recebe `--dev-node DiscordVpnLauncher` (o sanitizador remove qualquer `dev-node` que venha do config, para não duplicar a opção).

- **Não use `tapctl.exe` para isso.** Ele cria adaptadores pela SETUPAPI (`DiInstallDevice`), que exige o driver wintun já no *driver store* — em máquina que nunca teve OpenVPN instalado ele falha com `0xE0000203`. O `wintun.dll` embute o driver assinado e o instala sob demanda na primeira criação (mesmo mecanismo do WireGuard). Por isso o `tapctl.exe` deixou de ser embutido: ele não resolve o caso que interessa (máquina limpa).
- Criar o adaptador exige elevação — por isso isso vive no broker, e não no pai.
- **O adaptador tem o tempo de vida do HANDLE.** Fechar o handle (`Dispose` no `finally` do broker) o remove, e se o broker morrer de qualquer jeito o Windows fecha o handle por ele. É isso que faz o teste 6 da seção 12 (adaptador sumiu do `ipconfig`) passar, mesmo em crash. Matar o `openvpn.exe` sozinho desfaz só as rotas.

**Os binários não estão no git** (`.gitignore` cobre `**/Resources/*.exe` e `*.dll`). A **compilação funciona sem eles** (só emite aviso) e a falha aparece em runtime com mensagem explícita — ao investigar "não consegue subir a VPN", verifique antes de tudo se os recursos foram embutidos.

## Build

```powershell
dotnet publish DiscordVpnLauncher -c Release
```

Self-contained, single-file e `win-x64` já estão no `.csproj` — não precisa repetir as flags na linha de comando. Saída: `DiscordVpnLauncher\bin\Release\net8.0-windows\win-x64\publish\DiscordVpnLauncher.exe` (~60-70 MB — o runtime .NET embutido é custo aceito). Target `net8.0-windows` por causa das APIs Win-only.

## Detalhes de implementação fáceis de errar

- **Parser do CSV do VPNGate** (`https://www.vpngate.net/api/iphone/`, sem login/chave): tolerante a mudanças — colunas resolvidas por nome de cabeçalho, linhas `*` puladas. O base64 é lido do **último campo** da linha, não de um índice fixo: a coluna `Message` pode conter vírgulas e deslocar tudo o que vem depois dela.
- **Os configs do VPNGate são antigos e não sobem no OpenVPN 2.6 sem tratamento.** `VpnGateClient.PrepararConfig` remove opções que a 2.6 rejeita (`ncp-disable`, `keysize`, `ns-cert-type`, `tls-remote`, …), remove `explicit-exit-notify` (fatal quando o relay usa `proto tcp`), e injeta `data-ciphers`/`data-ciphers-fallback` + `allow-compression asym` quando o config usa compressão. Sem isso o OpenVPN aborta com "Unrecognized option" e **todos** os candidatos falham — sintoma que parece falta de relay, mas não é. Também injeta `connect-retry-max 2` para o broker desistir rápido e partir para o próximo candidato.
- **A confirmação de país não pode ser tiro único.** Quando o OpenVPN escreve `Initialization Sequence Completed`, rotas e DNS acabaram de mudar: a primeira requisição costuma morrer no socket, ou sair pela rota antiga e responder `BR`. `Orchestrator.ConfirmarPaisAsync` insiste por 20 s (2 s de assentamento, tentativas a cada 2 s) sobre **três** serviços — `ipinfo.io`, `ifconfig.co`, `api.country.is` — porque um deles fora do ar ou com rate limit também derrubava a sessão por motivo alheio à VPN. Já derrubou um túnel JP perfeitamente funcional; se voltar a falhar aqui, o `launcher.log` traz o erro de cada serviço.
- **Popup de falha:** `MessageBox` via P/Invoke de `user32.dll` (evita arrastar `System.Windows.Forms` para um console app). Botões: **Fechar** e **Continuar sem VPN** (abre o Discord no IP real). Retorno `6` = continuar, `7` = fechar. Dispara em: sem rede, lista `!= BR` vazia, `failed:all`, timeout da VPN, ou ipinfo retornando BR.
- **Lançar o Discord** pelo stub do Squirrel: `%LocalAppData%\Discord\Update.exe --processStart Discord.exe` (aponta sempre para a versão atual). Assume instalação padrão — instalação fora de `%LocalAppData%\Discord` precisa de fallback.
- **Pré-requisito manual do usuário** (documentar no README): desativar o auto-start do Discord. Se ficar ligado, o Discord abre pelo IP real antes do launcher e mata todo o propósito. O launcher não mexe nisso automaticamente.
- Um exe self-extracting que solta `openvpn.exe` e mexe em rede tende a acionar **SmartScreen/antivírus**. Aceito para uso próprio.
