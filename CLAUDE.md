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

- **Todo o retry de relay fica dentro do broker.** É o que garante 1 UAC só por sessão. Por isso o pai entrega uma *lista* de candidatos (5 relays `!= BR`, com `US` na frente e o resto do mais perto do Brasil para o mais longe), não um config único.
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
| Túnel firme (antes do Discord) | 2 checagens seguidas de adaptador com IPv4 + país `!= BR` | janela de 30 s |
| Discord capturou o IP | conexão do Discord com origem no IP do túnel que **sobrevive 6 s**, mais 5 s de margem | 60 s |
| Pai morreu (visto pelo broker) | `Process.GetProcessById(parentPid)` falha | poll 1 s |

A espera do pai pelo broker usa timeout **por inatividade** (45 s sem mudança de status), não por total: criar o adaptador na primeira vez instala driver, e o pior caso legítimo — isso mais 5 candidatos de 20 s — estoura qualquer prazo fixo curto. O broker publica `starting`/`trying:N` justamente para renovar esse prazo. Teto absoluto de 3 min.

## Invariantes que não podem ser quebrados

- **`app.manifest` tem que continuar `asInvoker`.** É o que mantém o pai em integridade média para o Discord herdar. Um `requireAdministrator` ali quebraria silenciosamente o objetivo inteiro — o Discord passaria a rodar como admin.
- **O túnel nunca fica aberto.** Dois mecanismos redundantes: `stop.signal` no `finally` do pai **e** watchdog do broker sobre o PID do pai.
- **Um UAC por execução** (no broker), zero UAC no Discord.
- **Matar todos os `Discord.exe` antes de relançar** — ele roda em vários processos, e sem o kill o relaunch só foca a janela existente, sem re-captura de IP. Com **zero** processos encerrados o passo é no-op e a espera pelo pipe sumir é pulada: o `discord-ipc-0` morre junto com o dono, então sem Discord aberto não existe pipe órfão — e esse é o caso comum de quem desativou o auto-start.
- **Mutex nomeado** para instância única do launcher (duas execuções concorrentes brigariam pela mesma `workDir` e pelas rotas).
- **Timeout do Discord não travar o processo:** se o pipe nunca aparecer, derrubar a VPN e encerrar de qualquer forma.
- **A VPN só cai depois de o Discord ter falado pelo túnel.** O pipe de IPC sobe junto com o processo, ~5 s antes de o app tocar no gateway — derrubar o túnel ali fazia o Discord registrar o IP real com a VPN tendo funcionado do início ao fim. `Orchestrator.EsperarCapturaDeIp` espera uma conexão do Discord com origem no IP do adaptador ([TcpTable.cs](DiscordVpnLauncher/TcpTable.cs)) que **sobreviva 6 s**, e só então segura mais 5 s (`DISCORD_VPN_LAUNCHER_ESPERA`). **A queda da conexão no teardown é esperada e inofensiva** — ver a seção da sacada, no topo.
- **O túnel precisa estar firme antes de o Discord subir, e "conectou" não prova isso.** `Initialization Sequence Completed` mais uma confirmação de país dizem que o túnel *chegou* a subir; relay gratuito que cai nos primeiros segundos, rota que oscila e adaptador que perde o IPv4 acontecem depois disso, e o `connected` do broker não volta atrás. `Orchestrator.EstabilizarTunelAsync` exige **duas checagens seguidas** — adaptador com IPv4 + país `!= BR` — dentro de 30 s. Falha isolada zera o contador em vez de condenar a sessão; estourar a janela cai no popup de falha. Lançar o Discord num túnel morto é o pior caso: ele registra o IP real e não há segunda chance sem matar e relançar. **O respiro fixo antes das checagens tem padrão zero** (`DISCORD_VPN_LAUNCHER_ESTABILIZACAO`): nasceu com 5 s de "assentamento", mas o assentamento já aconteceu — `ConfirmarPaisAsync` só retorna quando uma consulta responde de fato —, e espalhar a observação no tempo já é o papel das duas checagens separadas por 2 s. Espera fixa que não acrescenta sinal é só ping ruim cobrado do usuário; não reintroduza uma sem um sinal novo para justificá-la.
- **O teardown é um clique do usuário, com teto de tempo.** Depois da captura de IP, `NativeMethods.ShowTeardownPrompt` mostra um `MessageBox` de OK e segura o túnel até o clique — só quem está na frente da tela vê login pendente, update ou 2FA. Mas o clique **não pode ser a única saída**: estourado o teto (`DISCORD_VPN_LAUNCHER_TETO_MANUAL`, 10 min; `0` desativa o popup), o diálogo é dispensado de fora com `WM_COMMAND`/`IDOK` e o teardown segue. O diálogo vive em thread de background, então mesmo travado na tela ele não segura a saída do processo. O título dele é **próprio** (`"Discord VPN Launcher - VPN ligada"`) porque o `FindWindow` que o dispensa acertaria o console (`Console.Title`) ou outro popup se usasse o caption comum.
- **A janela de VPN é curta porque o custo dela recai no usuário.** Com o túnel de pé, todo o tráfego vai pelo relay no exterior e o ping em call fica impraticável — o usuário abre o Discord, entra em call e fica preso até o teardown. Por isso o sinal é a *persistência* da conexão (as conexões de boot do Discord duram menos de 1 s; a do gateway fica), e não uma margem fixa generosa. Ao mexer aqui, encurtar não é otimização cosmética e alongar não é grátis.
- **O executável se chama `Discord.exe`** (`AssemblyName`, ícone próprio). Consequência: `Process.GetProcessesByName("Discord")` devolve **o próprio launcher e o broker**. `DiscordController.IgnorarPid` recebe os dois PIDs e os filtra — sem isso o `MatarTudo` se suicida no passo 3, e as requisições do próprio launcher ao ipinfo (que saem pelo túnel) seriam lidas como "o Discord já está conversando". Qualquer código novo que enumere processos do Discord tem que passar por esse filtro.

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

## Instalador (Inno Setup)

`installer\DiscordVpnLauncher.iss` + `tools\build-installer.ps1` (que publica e chama o `ISCC`). Existe para distribuir a outras pessoas; o `.exe` solto continua funcionando sem instalar nada.

- **`PrivilegesRequired=lowest` é invariante**, pelo mesmo motivo do `asInvoker`: se o instalador rodasse elevado, o atalho e o "executar ao final" herdariam integridade alta e o Discord acabaria como admin.
- **O `AppId` é fixo** — é o que faz uma nova versão atualizar a instalação existente em vez de criar uma segunda entrada em "Aplicativos instalados".
- **O `.iss` tem que continuar salvo em UTF-8 *com BOM*.** Sem o BOM, o Inno lê o arquivo como ANSI e todo texto acentuado da interface vira caractere quebrado — **sem erro de compilação**, só no instalador pronto. Vale o mesmo para o `.ps1` que ele extrai. Se um editor ou script reescrever esses arquivos sem BOM, o estrago só aparece na tela do usuário final.
- **O ajuste do `settings.json` do Discord mora em [desativar-autostart.ps1](installer/desativar-autostart.ps1), não no `[Code]` Pascal.** O Pascal do Inno só lê arquivo como `AnsiString`; reescrever um JSON UTF-8 por ali corromperia acentos na configuração do usuário. O `.iss` extrai o script para o `{tmp}` (`Flags: dontcopy`) e o executa.
- Desativar o auto-start mexe em **dois** lugares e um sem o outro não resolve: `OPEN_ON_STARTUP` no `settings.json` (fonte da verdade — o Discord recria a chave `Run` a partir dela) e a chave `Run` do registro (que continuaria valendo até o Discord reabrir). E o Discord precisa estar **fechado**, porque reescreve o `settings.json` de memória ao sair.
- O desinstalador apaga `%LocalAppData%\DiscordVpnLauncher` (`[UninstallDelete]`), que fica fora de `{app}`.
- **A desinstalação oferece religar o auto-start do Discord.** A instalação alterou a configuração de um programa de terceiros; sair sem desfazer deixaria o usuário com um Discord que não abre mais sozinho e nada na máquina explicando por quê. Pergunta em vez de decidir — nos dois sentidos seria errado decidir sozinho. Roda em `usUninstall`, **antes** da remoção dos arquivos (o `.ps1` ainda precisa existir), e é por isso que ele é instalado em `{app}` em vez de `dontcopy`.
- **A pergunta só aparece para quem marcou a caixa.** `RegisterPreviousData` grava um marcador na chave de desinstalação; sem ele, a pergunta afirmaria algo falso e um "sim" distraído ligaria o auto-start de quem o mantinha desligado de propósito. Ao ler esse marcador, o nome tem o prefixo **`Inno Setup CodeFile: `** — `SetPreviousData` o acrescenta, e ler pelo nome cru devolve sempre falso, de forma silenciosa (o desinstalador simplesmente não pergunta nada).

Três armadilhas do Inno que só aparecem em compilação ou em runtime, todas já custaram uma rodada:

| Armadilha | Sintoma |
|---|---|
| `.iss` sem BOM | acentos quebrados no instalador pronto, **sem** erro de compilação |
| linha começando com `#` | `Unknown preprocessor directive` — os `#13#10` ficam no fim da linha anterior |
| `{app}` dentro de comentário `{ }` do Pascal | o `}` fecha o comentário no meio e o texto vira código |

## Detalhes de implementação fáceis de errar

- **Parser do CSV do VPNGate** (`https://www.vpngate.net/api/iphone/`, sem login/chave): tolerante a mudanças — colunas resolvidas por nome de cabeçalho, linhas `*` puladas. O base64 é lido do **último campo** da linha, não de um índice fixo: a coluna `Message` pode conter vírgulas e deslocar tudo o que vem depois dela.
- **A escolha do relay define o ping da call, não só o do túnel.** O Discord escolhe a região de voz pelo IP registrado no login, e essa escolha **sobrevive ao teardown**: um relay japonês deixa o usuário falando com um servidor no Japão pela internet brasileira (~300 ms medidos) até o próximo login. Por isso `VpnGateClient.SelecionarCandidatos` ordena por `PaisesPorProximidade` e só usa `Score`/`Ping` para desempatar *dentro* do mesmo país. Ranquear por `Score` puro entregava o pior caso possível: a lista do VPNGate é dominada pelo Japão (52 de 97 relays numa medição), então os candidatos saíam `JP, JP, JP, JP, JP`. Três decisões sustentam a ordem atual (15.11 e 15.12 do plano):
  - **`US` é o primeiro item da lista, por decisão de produto, não por distância.** A US East fica em ~120 ms do Brasil e é a única região com oferta grande e estável no VPNGate; a vizinhança sul-americana é magra e costuma estar fora do ar, gastando tentativas de 20 s do broker antes de a sessão chegar em algo que conecta. Depois do `US`, a ordem segue por distância até o Brasil.
  - **Teto de 3 candidatos por país** (`MaximoPorPais`), senão os 5 slots sairiam todos dos EUA e o retry deixaria de ser retry. A seleção faz duas passadas: a primeira respeita o teto, a segunda preenche as vagas restantes quando não há países suficientes.
  - **O último slot fica reservado ao melhor `Score` global**, para que uma vizinhança morta vire ping ruim e não sessão falhada.
- **Os configs do VPNGate são antigos e não sobem no OpenVPN 2.6 sem tratamento.** `VpnGateClient.PrepararConfig` remove opções que a 2.6 rejeita (`ncp-disable`, `keysize`, `ns-cert-type`, `tls-remote`, …), remove `explicit-exit-notify` (fatal quando o relay usa `proto tcp`), e injeta `data-ciphers`/`data-ciphers-fallback` + `allow-compression asym` quando o config usa compressão. Sem isso o OpenVPN aborta com "Unrecognized option" e **todos** os candidatos falham — sintoma que parece falta de relay, mas não é. Também injeta `connect-retry-max 2` para o broker desistir rápido e partir para o próximo candidato.
- **A confirmação de país não pode ser tiro único.** Quando o OpenVPN escreve `Initialization Sequence Completed`, rotas e DNS acabaram de mudar: a primeira requisição costuma morrer no socket, ou sair pela rota antiga e responder `BR`. `Orchestrator.ConfirmarPaisAsync` insiste por 20 s (2 s de assentamento, tentativas a cada 2 s) sobre **três** serviços — `ipinfo.io`, `ifconfig.co`, `api.country.is` — porque um deles fora do ar ou com rate limit também derrubava a sessão por motivo alheio à VPN. Já derrubou um túnel JP perfeitamente funcional; se voltar a falhar aqui, o `launcher.log` traz o erro de cada serviço.
- **O ruído de retry não vai para o console.** As tentativas que falham logo após o túnel subir (`Este host não é conhecido`, DNS ainda trocando) são o comportamento normal e se resolvem na volta seguinte — mas na 1.3 elas apareciam na tela e usuários reportaram como erro. O detalhe de cada tentativa passou para `Log.Diag`, que escreve **só** no arquivo; o console recebe uma linha calma e única (`Aguardando as rotas da VPN assentarem...`) apenas quando a demora passa de 8 s, o que o caso normal (~5 s) nunca alcança. Vale o mesmo para as checagens de `EstabilizarTunelAsync`. Ao mexer aqui: `Log.Diag` é para o que se resolve sozinho — o que exige ação do usuário, ou explica uma falha real, continua em `Warn`/`Error`.
- **Popup de falha:** `MessageBox` via P/Invoke de `user32.dll` (evita arrastar `System.Windows.Forms` para um console app). Botões: **Fechar** e **Continuar sem VPN** (abre o Discord no IP real). Retorno `6` = continuar, `7` = fechar. Dispara em: sem rede, lista `!= BR` vazia, `failed:all`, timeout da VPN, ou ipinfo retornando BR.
- **Lançar o Discord** pelo stub do Squirrel: `Update.exe --processStart Discord.exe` (aponta sempre para a versão atual). A localização **não** é fixa: `DiscordController.LocalizarLauncher` tenta, nessa ordem, a env var, uma escolha manual lembrada (`discord-path.txt` na raiz da pasta de dados, fora de `work\` porque essa é limpa a cada sessão), o **registro** (`Uninstall\Discord\InstallLocation` e o handler do protocolo `discord://`), o caminho padrão, e por fim um seletor de arquivo. O registro vem antes do caminho padrão porque é ele que resolve instalação em outro HD sem ninguém configurar nada — e resolve de forma sempre atual, ao contrário de um campo fixo no instalador, que congelaria e passaria a mentir na primeira reinstalação do Discord.
- **`Montar` deduz a variante pelo nome da pasta** (`DiscordPTB\Update.exe` → `--processStart DiscordPTB.exe`). Passar `Discord.exe` fixo faria o Update.exe do PTB tentar abrir um app que não existe ali.
- **`--diagnostico`** imprime onde o Discord foi encontrado e o estado dos binários, sem tocar em nada. É o primeiro comando a pedir para quem relata problema — resolve a maior parte dos casos sem precisar de log.
- **Pré-requisito manual do usuário** (documentar no README): desativar o auto-start do Discord. Se ficar ligado, o Discord abre pelo IP real antes do launcher e mata todo o propósito. O launcher não mexe nisso automaticamente.
- Um exe self-extracting que solta `openvpn.exe` e mexe em rede tende a acionar **SmartScreen/antivírus**. Aceito para uso próprio.
