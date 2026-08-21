# Plano de Ação — Discord VPN Launcher (C#)

## Objetivo

Um único `.exe` que o usuário abre manualmente. Ele sobe uma VPN gratuita em um servidor **fora do Brasil**, lança o Discord por baixo dessa VPN (para o Discord "fotografar" o IP não-brasileiro na inicialização), e **derruba a VPN** assim que o Discord termina de se registrar. Discord roda **sem privilégio de administrador**.

A ferramenta só faz sentido por causa de um comportamento confirmado em uso: quando a VPN cai, o Discord reconecta pelo IP real brasileiro e **continua funcionando com o IP não-brasileiro já registrado**. A reconexão não refaz o registro — por isso basta cobrir o login, e não a sessão inteira (ver 15.7).

---

## 1. Decisões travadas (referência rápida)

| Item | Decisão |
|---|---|
| Linguagem | C#, console app |
| Distribuição | `.exe` **self-contained, single-file** (runtime .NET embutido) |
| Execução | Manual — usuário abre. **Sem** inicialização automática / Agendador |
| VPN | VPNGate + OpenVPN (único gratuito sem login que escolhe país via CLI) |
| Filtro de país | Qualquer relay com `CountryShort != BR` |
| Binários OpenVPN | `openvpn.exe` + `wintun.dll` **embutidos** no exe, extraídos no 1º uso |
| Elevação | Pai **não-elevado**; broker **elevado** só para a VPN (1 UAC/sessão) |
| Discord | Lançado pelo pai não-elevado → roda limpo |
| Sinal "VPN pronta" | Linha `Initialization Sequence Completed` no log do OpenVPN |
| Sinal "Discord pronto" | Named pipe `\\.\pipe\discord-ipc-0` |
| Falha ao conectar | Popup "Erro ao conectar em outro país" → **Fechar** / **Continuar sem VPN** |
| Pré-requisito do usuário | Desativar o auto-start do próprio Discord (manual) |

---

## 2. A peça central: por que "pai não-elevado + broker elevado" (e não só openvpn elevado)

Refinamento importante em cima do que discutimos. Um processo **não-elevado (integridade média) não consegue matar um processo elevado (integridade alta)** — o Windows nega por diferença de nível de integridade. Se o filho elevado fosse o próprio `openvpn.exe`, o pai não-elevado **não conseguiria derrubá-lo** no fim. O cleanup quebraria.

Solução: o filho elevado é uma **segunda instância do próprio exe rodando em "modo broker"**. O mesmo binário, dois modos:

- **Modo Orquestrador (pai, não-elevado):** faz tudo que *não* precisa de admin — baixar lista VPNGate, filtrar, escrever config, matar/lançar Discord, checar IP, popup.
- **Modo Broker (filho, elevado, 1 UAC):** faz tudo que *precisa* de admin — subir o `openvpn.exe`, monitorar, e **derrubar o túnel** no fim. Como é ele (elevado) que criou e é dono do processo do OpenVPN, ele consegue matá-lo e restaurar as rotas.

O pai não mata o OpenVPN. O pai **sinaliza** o broker, e o broker (elevado) faz a limpeza. O broker também vigia o PID do pai: se o pai morrer/crashar, o broker derruba a VPN sozinho e encerra.

```
exe --modo-orquestrador  (integridade média, SEM UAC)
 │
 ├─ mata Discord aberto
 ├─ baixa lista VPNGate, filtra != BR, decodifica N candidatos (.ovpn)
 ├─ relança a si mesmo:  exe --broker <dir> <pid-do-pai>   ← ÚNICO UAC aqui
 │        └── (broker, integridade alta)
 │              ├─ tenta candidato 1 → openvpn --config c1.ovpn --log ...
 │              ├─ lê "Initialization Sequence Completed" → escreve status "connected"
 │              ├─ (se falhar, tenta candidato 2, 3...)  ← retry fica AQUI, sem novo UAC
 │              └─ vigia PID do pai / espera sinal de stop
 │
 ├─ (pai) lê status "connected" → confirma país via ipinfo (!= BR)
 ├─ lança Discord (NORMAL, não-elevado) → herda integridade média → limpo
 ├─ espera o pipe \\.\pipe\discord-ipc-0 (com timeout)
 ├─ escreve sinal de stop → broker derruba openvpn, restaura rotas, sai
 └─ finally: garante o sinal de stop mesmo se der exceção
```

**Consequência de design:** todo o retry de relay acontece *dentro do broker*, para manter **1 UAC só**. Por isso o pai entrega uma **lista de candidatos** (5 relays `!= BR`, ordenados do mais perto do Brasil para o mais longe), não um único config.

---

## 3. Pré-requisito por conta do usuário (documentar no README)

O Discord se auto-inicia no login do Windows por padrão. Se ficar ligado, ele abre pelo IP real **antes** do launcher e mata todo o propósito.

**Ação manual, uma vez:** Discord → Configurações → Windows Settings → desligar "Open Discord" / "Iniciar com o sistema". (E conferir a entrada dele em `Gerenciador de Tarefas → Inicializar`.)

O launcher não faz isso automaticamente (exigiria mexer no registro/startup do usuário). Fica como passo de setup documentado.

---

## 4. Estrutura do projeto (VS Code)

```
DiscordVpnLauncher/
├─ DiscordVpnLauncher.csproj
├─ Program.cs              # entrypoint: decide modo (orquestrador vs broker) pelos args
├─ Orchestrator.cs         # fluxo do pai não-elevado
├─ VpnBroker.cs            # fluxo do filho elevado (gerencia openvpn)
├─ VpnGateClient.cs        # baixar/parsear/filtrar a lista, decodificar .ovpn
├─ DiscordController.cs    # matar, lançar, detectar o pipe discord-ipc-0
├─ ElevationHelper.cs      # relançar a si mesmo com verbo "runas"
├─ NativeMethods.cs        # P/Invoke: MessageBox (user32)
├─ Paths.cs               # pasta de trabalho em %LocalAppData%\DiscordVpnLauncher
└─ Resources/
   ├─ openvpn.exe          # EmbeddedResource
   └─ wintun.dll           # EmbeddedResource
```

### `.csproj` (pontos-chave)

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net8.0-windows</TargetFramework>   <!-- ou a LTS atual -->
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <PublishSingleFile>true</PublishSingleFile>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>

<ItemGroup>
  <EmbeddedResource Include="Resources\openvpn.exe" />
  <EmbeddedResource Include="Resources\wintun.dll" />
</ItemGroup>
```

---

## 5. Recursos embutidos (OpenVPN)

- Obter `openvpn.exe` e `wintun.dll` de uma instalação oficial do **OpenVPN 2.6+** (o `openvpn.exe` do diretório `bin` e o `wintun.dll` que acompanha). O 2.6 usa **wintun por padrão** — evita o driver TAP clássico e o inferno de "instalar driver de rede".
- Embutir os dois como `EmbeddedResource`.
- **No 1º uso:** extrair para `%LocalAppData%\DiscordVpnLauncher\bin\` se ainda não existir (é o "verifica se a VPN está baixada, se não baixe" — sem instalador, sem download externo).
- `wintun.dll` precisa estar na mesma pasta do `openvpn.exe`.
- Rodar o OpenVPN com `--windows-driver wintun` explícito (garantia, caso o default mude).

**Nota:** *criar* o adaptador wintun e mexer nas rotas continua sendo operação privilegiada — por isso só o broker elevado invoca o `openvpn.exe`. O bundle elimina a instalação de driver, não a necessidade de elevação para *usar* o adaptador.

---

## 6. Fluxo de execução detalhado (modo Orquestrador)

1. **Preparar ambiente.** Criar `%LocalAppData%\DiscordVpnLauncher\` (subpastas `bin`, `work`). Extrair `openvpn.exe`/`wintun.dll` para `bin\` se faltarem.
2. **Checar rede básica.** Um ping/HTTP rápido (ex.: `ipinfo.io`) para confirmar internet. Sem rede → popup de falha direto.
3. **Matar Discord aberto.** Encerrar **todos** os processos `Discord.exe` (ele roda em vários). Sem isso, relançar só foca a janela existente → sem re-captura de IP. **Nenhum processo encerrado → pular direto para o passo 4**, sem esperar o pipe sumir: o `discord-ipc-0` pertence ao processo do Discord e morre com ele, então sem Discord aberto não há pipe órfão a aguardar — ver 15.12.
4. **Baixar lista VPNGate.** `GET https://www.vpngate.net/api/iphone/` (CSV). Sem login, sem chave.
5. **Filtrar e ranquear.** Descartar linhas com `CountryShort == "BR"`. Ordenar pela preferência de país (`VpnGateClient.PaisesPorProximidade`): **`US` primeiro**, o resto por distância geográfica até o Brasil; `Score`/uptime e depois `Ping` desempatam *dentro* do mesmo país. Pegar os **top N (≈5)**, com no máximo **3 candidatos do mesmo país** e o último slot reservado ao melhor `Score` da lista inteira como rede de segurança — ver 15.11 e 15.12.
6. **Se lista `!= BR` vazia** → popup de falha (raro, mas trate).
7. **Decodificar configs.** Cada candidato tem o `.ovpn` inteiro em base64 na última coluna (`OpenVPN_ConfigData_Base64`), com CA/cert/key inline — self-contained, sem arquivos extras. Decodificar e gravar `work\cand1.ovpn`, `cand2.ovpn`, ...
8. **Subir o broker (1 UAC).** Relançar a si mesmo elevado: `exe --broker <workDir> <pidDoPai>`. O `runas` dispara **um** UAC.
9. **Aguardar conexão.** Poll em `work\vpn-status.txt` (escrito pelo broker):
   - `connected:<país>` → seguir.
   - `failed:all` ou **timeout** (ex.: 45 s) → popup de falha.
10. **Confirmar país (ipinfo).** `GET https://ipinfo.io/country`. Se `!= BR` → ok. Se `== BR` (label do VPNGate mentiu) → tratar como falha → popup. *(Passo simples e único; não entra no loop de retry, para não complicar.)*
10b. **Estabilizar antes de lançar.** Respiro fixo (5 s, `DISCORD_VPN_LAUNCHER_ESTABILIZACAO`) e então **duas checagens seguidas** — adaptador do túnel com IPv4 + país `!= BR` — dentro de uma janela de 30 s. Falha isolada zera o contador; estourar a janela é falha de sessão (popup). Uma confirmação única prova que o túnel *chegou* a subir, não que ele está firme — ver 15.12.
11. **Lançar Discord (não-elevado).** Via stub do Squirrel: `%LocalAppData%\Discord\Update.exe --processStart Discord.exe` (aponta sempre para a versão atual). `UseShellExecute` normal → herda integridade média do pai.
12. **Esperar Discord pronto.** Poll pela existência de `\\.\pipe\discord-ipc-0` (timeout ≈ 30 s). Apareceu = inicializou.
12b. **Popup de desligamento manual.** `MessageBox` com um botão **OK** ("desligar a VPN agora"), exibido depois da captura de IP. Quem está na frente da tela é a única parte do sistema que enxerga login pendente, update ou 2FA. Teto de 10 min (`DISCORD_VPN_LAUNCHER_TETO_MANUAL`, `0` desativa o popup): estourado, o dialogo é dispensado por `WM_COMMAND`/`IDOK` e o teardown segue — ver 15.12.
13. **Derrubar a VPN.** Escrever `work\stop.signal`. O broker mata o OpenVPN, restaura rotas, apaga configs e sai.
14. **`finally`:** garantir que `stop.signal` foi escrito mesmo em caso de exceção, para nunca deixar túnel aberto.

---

## 7. Fluxo do Broker (modo elevado)

1. Parse dos args: `workDir`, `parentPid`.
2. Ler os candidatos `cand*.ovpn` da `workDir`.
3. Para cada candidato, em ordem:
   - `openvpn.exe --config candN.ovpn --windows-driver wintun --log work\openvpn.log`.
   - Ler o log procurando `Initialization Sequence Completed` (sucesso) ou erro fatal / timeout por candidato (≈ 20 s).
   - Sucesso → escrever `vpn-status.txt = connected:<país>` e ir para o passo 4.
   - Falha → matar o processo, tentar o próximo.
   - Todos falharam → `vpn-status.txt = failed:all` e encerrar.
4. **Loop de vigília** até um destes ocorrer:
   - Existe `work\stop.signal` → encerrar OpenVPN.
   - `Process.GetProcessById(parentPid)` não existe mais (pai morreu) → encerrar OpenVPN.
5. **Teardown:** terminar o `openvpn.exe` (ele restaura rotas ao sair; se necessário, `Kill` — o adaptador wintun some e leva as rotas junto), apagar `cand*.ovpn` e o log, e sair.

**Por que redireciono log em arquivo, e não stdout:** `runas` exige `UseShellExecute = true`, o que **impede** redirecionar o stdout do processo elevado. Por isso o OpenVPN escreve em `--log` e o broker lê o arquivo. Mesma lógica para o `vpn-status.txt` (o pai não-elevado lê o que o broker elevado escreveu).

---

## 8. Detecção e sinais (sem `sleep` fixo)

| O quê | Sinal concreto | Timeout sugerido |
|---|---|---|
| VPN subiu | `Initialization Sequence Completed` no log | 20 s/candidato |
| Está fora do BR | `ipinfo.io/country != BR` | 5 s |
| Discord subiu | pipe `\\.\pipe\discord-ipc-0` existe | 30 s |
| Pai morreu (broker) | `Process.GetProcessById(pid)` lança/retorna morto | poll 1 s |

Detecção do pipe: enumerar `\\.\pipe\` (via `Directory.GetFiles(@"\\.\pipe\")`) e procurar `discord-ipc-0`, ou tentar conectar um `NamedPipeClientStream` rápido. Existência = pronto.

---

## 9. Tratamento de falha (popup)

Dispara quando: sem rede, lista `!= BR` vazia, `failed:all` do broker, timeout da VPN, ou ipinfo retornou BR.

- **Texto:** "Erro ao conectar em outro país."
- **Botões:** **Fechar** (encerra tudo, não abre Discord) e **Continuar sem VPN** (abre o Discord no **IP real brasileiro** — a sessão vai gravar esse IP, já que o Discord fotografa uma vez).
- **Implementação:** `MessageBox` via P/Invoke de `user32.dll` (evita arrastar `System.Windows.Forms` para um console app). `MB_YESNO` ou `MB_OKCANCEL` mapeando os dois botões.

```csharp
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
// type: MB_ICONWARNING | MB_YESNO ; retorno 6 = Sim/Continuar, 7 = Não/Fechar
```

---

## 10. Robustez / cleanup (não pode faltar)

- **Túnel nunca fica aberto:** garantido por dois mecanismos redundantes — `stop.signal` no `finally` do pai **e** watchdog do broker sobre o PID do pai.
- **Discord já aberto:** kill obrigatório antes de relançar (senão sem re-init).
- **Timeout do Discord:** se o pipe nunca aparecer, encerrar mesmo assim (derrubar VPN, opcionalmente avisar) — não travar.
- **Instância única do launcher:** um mutex nomeado evita duas execuções concorrentes mexendo na mesma `workDir`/rotas.
- **Retry de relay:** dentro do broker, para manter 1 UAC.

---

## 11. Build & publish

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true
```

Saída: um `.exe` único em `bin\Release\net8.0-windows\win-x64\publish\`.

**Notas:**
- Self-contained → **~60-70 MB** (runtime .NET dentro). É o custo aceito para "roda em qualquer Windows sem pré-requisito".
- Um exe self-extracting que solta `openvpn.exe` e mexe em rede tende a acionar **SmartScreen / antivírus**. Para uso próprio, ok; para distribuir, considerar **assinatura de código** mais adiante.
- `net8.0-windows` (ou a LTS atual) por causa das APIs Win-only.

---

## 12. Validação (antes de considerar pronto)

1. **Extração:** apagar `%LocalAppData%\DiscordVpnLauncher\bin` e rodar → confirma que os binários são extraídos.
2. **1 UAC só:** rodar do início ao fim → deve aparecer **um** prompt de UAC (no broker), e nenhum no Discord.
3. **Discord não-elevado:** com o Discord aberto pelo launcher, testar **arrastar um arquivo** para a janela dele → tem que funcionar (prova que não está como admin).
4. **País:** durante a janela conectada, `ipinfo.io/country` deve retornar `!= BR`.
5. **Fim-a-fim:** confirmar que, após o launcher derrubar a VPN, o estado capturado pelo Discord persiste como esperado ao entrar/sair de call (premissa já validada por você — reconfirmar no binário final).
6. **Cleanup em crash:** matar o pai à força no meio → o broker deve derrubar o túnel sozinho (checar em `ipconfig`/rotas que o adaptador wintun sumiu).
7. **Caminho de falha:** forçar (ex.: sem rede) → popup aparece; "Continuar" abre Discord sem VPN; "Fechar" encerra limpo.

---

## 13. Riscos e pontos abertos

- **Escassez/instabilidade de relays VPNGate:** mitigada pelo uso efêmero + retry por lista de candidatos. Pode, em dia ruim, cair no popup de falha — comportamento esperado.
- **Mudança no formato da API do VPNGate:** o parser de CSV precisa ser tolerante (colunas por índice/nome de cabeçalho, pular linhas de comentário `*`).
- **Caminho do Discord:** assume instalação padrão em `%LocalAppData%\Discord`. Se o usuário instalou noutro lugar, precisa de fallback/config.
- **Antivírus/SmartScreen:** ver seção 11.
- **Premissa do Discord "fotografa uma vez":** todo o valor da ferramenta depende disso. Já validado por você; o teste 5 reconfirma no produto final.

---

## 14. Checklist de implementação (ordem sugerida)

1. [ ] Esqueleto do projeto + `.csproj` (self-contained/single-file).
2. [ ] `Program.cs`: roteamento de modo (orquestrador vs `--broker`).
3. [ ] `Paths.cs` + extração dos recursos embutidos.
4. [ ] `VpnGateClient`: download, parse CSV, filtro `!= BR`, decode base64 → `.ovpn`.
5. [ ] `ElevationHelper`: relançar com `runas`.
6. [ ] `VpnBroker`: rodar OpenVPN, ler log, escrever status, retry, watchdog, teardown.
7. [ ] `Orchestrator`: orquestração completa + `finally`/mutex.
8. [ ] `DiscordController`: kill, launch, detectar `discord-ipc-0`.
9. [ ] `NativeMethods` + popup de falha.
10. [ ] Checagem ipinfo.
11. [ ] Rodar a bateria de validação (seção 12).
12. [ ] README com o pré-requisito de desativar o auto-start do Discord.

---

## 15. Correções descobertas na implementação

Três premissas deste plano não se sustentaram na prática. Ficam registradas aqui porque afetam as seções 5, 7 e 12.

### 15.1 `openvpn.exe` + `wintun.dll` não bastam (seção 5)

O `openvpn.exe` linka contra as DLLs do OpenSSL: sem elas ele sai com `0xC0000135` (*DLL not found*) **antes de escrever qualquer linha de log** — sintoma que parece "o OpenVPN não iniciou, sem motivo". O conjunto embutido tem 6 arquivos (eram 7 até a 15.4 dispensar o `tapctl.exe`):

`openvpn.exe`, `wintun.dll`, `libcrypto-3-x64.dll`, `libssl-3-x64.dll`, `libpkcs11-helper-1.dll`, `vcruntime140.dll`

Como a lista muda entre versões do OpenVPN, o `.csproj` embute `Resources\*` por curinga em vez de listar arquivos. O `tools/get-openvpn-binaries.ps1` monta a pasta sem instalar nada na máquina (`msiexec /a`) e valida a assinatura Authenticode de cada arquivo.

### 15.2 O MSI do OpenVPN não contém `wintun.dll`

Verificado nos 71 arquivos que o MSI extrai, inclusive com `ADDLOCAL=ALL`: o `wintun.dll` não está lá — o instalador trata o driver por outro caminho. Ele vem do upstream ([wintun.net](https://www.wintun.net), assinado pela WireGuard LLC), que é a origem do arquivo de qualquer forma.

### 15.3 O OpenVPN **não cria** o adaptador wintun (seções 7 e 12)

Esta é a correção que muda o desenho. Rodando um candidato já sanitizado, o OpenVPN completa o TLS e recebe o `PUSH_REPLY`, e então morre em:

```
open_tun
There are no TAP-Windows, Wintun or ovpn-dco adapters on this system.
You should be able to create an adapter by using tapctl.exe utility.
Exiting due to fatal error
```

Consequências:

- **O broker precisa criar o adaptador** com `tapctl create --hardware-id wintun --name DiscordVpnLauncher` antes de tentar os candidatos, e passar `--dev-node` ao OpenVPN. O `tapctl.exe` tem manifest `requireAdministrator`, então só roda elevado — encaixa no broker sem gerar um segundo UAC, já que um filho de processo elevado herda o token.
- **Matar o OpenVPN não faz o adaptador desaparecer.** A afirmação da seção 7 ("o adaptador wintun some e leva as rotas junto") está errada: matar o processo desfaz as rotas, mas o adaptador persiste. O teardown do broker chama `tapctl delete` explicitamente — é o que faz o teste 6 da seção 12 (adaptador sumiu do `ipconfig`) passar.
- **O timeout de 45 s da seção 6/9 é curto demais.** Criar o adaptador na primeira vez instala o driver, e o pior caso legítimo é isso mais cinco candidatos de 20 s. O pai passou a usar timeout por **inatividade** (45 s sem mudança de status do broker, que publica `starting`/`trying:N`), com teto absoluto de 3 min.

### 15.4 O `tapctl.exe` não cria o adaptador em máquina sem OpenVPN instalado (corrige 15.3)

A correção anterior funcionava só onde o driver wintun já estava no *driver store*. Em um Windows limpo, `tapctl create --hardware-id wintun` falha com `0xE0000203`: ele cria adaptadores pela SETUPAPI (`DiInstallDevice`), que **exige o driver já instalado** — exatamente o que este projeto quer evitar.

Quem sabe instalar o driver sob demanda é o próprio `wintun.dll`: ele embute o driver assinado e o instala na primeira criação de adaptador (mesmo mecanismo do WireGuard para Windows). Por isso o adaptador passou a ser criado por P/Invoke em [WintunAdapter.cs](DiscordVpnLauncher/WintunAdapter.cs) (`WintunCreateAdapter`), e não mais por processo externo.

Ganho colateral no teardown: **o adaptador tem o tempo de vida do HANDLE**. Fechar o handle o remove, e se o broker morrer de qualquer forma o Windows fecha o handle por ele — o lixo de rede some sozinho, sem depender de um `delete` explícito dar certo.

Com isso o `tapctl.exe` saiu do bundle e do `tools/get-openvpn-binaries.ps1` — são 6 arquivos embutidos, não 7. Uma cópia dele pode ter sobrado em `bin` de uma versão anterior; é inofensiva e nada mais a usa.

### 15.5 A confirmação de país precisa de janela, não de tiro único (seção 6, passo 10)

O passo 10 era "simples e único" de propósito. Na prática ele falha por corrida: quando o OpenVPN escreve `Initialization Sequence Completed`, as rotas e o DNS **acabaram** de ser trocados, e a primeira requisição HTTP costuma morrer no socket — ou sair ainda pela rota antiga e responder `BR`. O resultado é uma sessão descartada com a VPN funcionando perfeitamente (observado com o túnel em JP já completo).

Passou a ser uma **janela de confirmação de 20 s** (2 s de assentamento, tentativas a cada 2 s) sobre uma lista de serviços — `ipinfo.io/country`, `ifconfig.co/country-iso`, `api.country.is` — porque um serviço fora do ar, bloqueado pelo relay ou aplicando rate limit também derrubava a sessão por motivo alheio à VPN. Só falha se a janela inteira passar; `BR` persistente e "nenhum serviço respondeu" são falhas distintas na mensagem ao usuário.

### 15.6 O pai não deixava rastro em disco

Só o broker espelhava log em arquivo. Como o console do pai some quando a janela fecha, toda falha do lado não-elevado (justamente a da 15.5) era invisível na hora do diagnóstico. O pai agora espelha em `work\launcher.log`, criado depois da limpeza da `work\` para não ser apagado no mesmo ciclo.

### 15.7 O pipe de IPC não é sinal de "IP capturado" (seções 6 e 11)

O passo 12 tratava `\.\pipe\discord-ipc-0` como "Discord pronto" e derrubava a VPN em seguida. Medido: o pipe aparece **~5 s** depois do lançamento, e nesse ponto o app ainda nem falou com o gateway — o `launcher.log` de uma sessão com túnel JP confirmado mostra `Discord inicializou sob a VPN` e `tunel desfeito` no mesmo segundo. Ou seja, o IP fotografado era o real, com a VPN tendo funcionado perfeitamente do começo ao fim.

O contraste que fechou o diagnóstico foi o teste manual com Proton VPN (conectar, abrir o Discord, esperar carregar, desconectar): funciona. A diferença não é a VPN — é *quando* ela cai.

O sinal correto tem duas partes, ambas em `Orchestrator.EsperarCapturaDeIp`:

1. **Uma conexão ESTABLISHED do Discord com endereço local igual ao IP do adaptador do túnel** ([TcpTable.cs](DiscordVpnLauncher/TcpTable.cs), via `GetExtendedTcpTable`). Prova as duas coisas de uma vez: o app saiu do boot e está conversando, e a conversa passa por dentro da VPN. Nenhuma API gerenciada responde "por qual interface este processo está falando"; o endereço local da tabela TCP responde. Não exige elevação.
2. **Uma folga fixa depois disso** (padrão 30 s, ajustável por `DISCORD_VPN_LAUNCHER_ESPERA`), porque o handshake de login continua depois do primeiro socket abrir, e é no login que o IP é registrado.

Nenhuma das duas é fatal: falhando o sinal, o launcher registra o aviso e vai para o teardown assim mesmo — o invariante "o túnel nunca fica aberto" continua acima de tudo.

**O que acontece no teardown, confirmado:** a conexão do Discord cai junto com o túnel (o IP de origem some do adaptador) e ele reconecta pelo IP real brasileiro — continuando a funcionar normalmente, com o IP não-brasileiro já registrado. A reconexão **não** refaz o registro.

Isso deixou de ser risco e virou a premissa validada do produto: é exatamente por isso que basta uma janela curta de VPN cobrindo o login, e não uma VPN permanente. Ver a seção "A sacada" no [CLAUDE.md](CLAUDE.md).

### 15.8 A janela de VPN precisa ser curta, e o sinal certo é a *persistência* da conexão

A 15.7 resolveu a captura do IP com uma folga fixa de 30 s. Funciona, mas o custo cai no usuário: enquanto o túnel está de pé, **todo** o tráfego da máquina passa pelo relay no exterior. Na prática o usuário abre o Discord, entra numa call e fica com ping impraticável até o teardown — sem saber por quê nem por quanto tempo.

A folga fixa era um chute para cima porque não havia um sinal melhor. Há: o Discord abre várias conexões curtas no boot (API, CDN, assets) que nascem e morrem em menos de um segundo, e **uma** que fica de pé — a sessão do gateway, que só se mantém depois de o login concluir. Como `GetExtendedTcpTable` dá porta local e destino, dá para identificar cada socket entre duas leituras e medir há quanto tempo ele existe.

`DiscordController.EsperarSessaoPeloTunel` espera uma conexão pelo IP do túnel **sobreviver 6 s**; sockets que somem têm sua contagem descartada, para que uma sequência de conexões curtas não se acumule como se fosse uma persistente. Depois disso, 5 s de margem (era 30) e teardown. Resultado: ~15 s de VPN após o Discord abrir, contra ~40 s.

O console mostra contagem regressiva durante a margem e termina com `VPN desligada, ping normal` — sem isso o usuário não tem como saber quando pode entrar em call.

**Ao mexer nesses números:** encurtar não é otimização cosmética (é o que evita o único incômodo real da ferramenta) e alongar não é grátis (é ping do exterior na cara do usuário).

### 15.9 O executável se chama `Discord.exe` — e isso quase se auto-destrói

Por pedido de uso, o `AssemblyName` virou `Discord` e o exe ganhou o ícone do Discord (`ApplicationIcon`, [discord_icon.ico](DiscordVpnLauncher/discord_icon.ico)). Duas consequências não-óbvias:

- `Process.GetProcessesByName("Discord")` passa a devolver **o próprio launcher e o broker**. Sem filtro, o passo 3 (matar todos os processos do Discord) mataria o próprio launcher antes de qualquer coisa acontecer. `DiscordController.IgnorarPid` registra o PID próprio e o do broker, e tanto o `MatarTudo` quanto a detecção de tráfego os ignoram — esta última também importa, porque as requisições do próprio launcher ao ipinfo saem pelo túnel e seriam lidas como "o Discord já está conversando".
- O `RootNamespace` ficou **fixo** em `DiscordVpnLauncher` no `.csproj`. Os `EmbeddedResource` são nomeados a partir dele (`DiscordVpnLauncher.Resources.*`, que é o que `Paths.ExtractEmbeddedBinaries` procura); deixar o namespace seguir o `AssemblyName` renomearia os recursos e quebraria a extração dos binários em runtime, sem erro de compilação.

---

## 16. Instalador (Inno Setup)

Acréscimo ao plano original, que previa distribuição só pelo `.exe` solto. Com outras pessoas usando a ferramenta, um `setup.exe` resolve atalho, ícone, desinstalação e — o mais importante — o **pré-requisito manual da seção 3**, que até aqui dependia de o usuário lembrar de desativar o auto-start do Discord.

`installer/DiscordVpnLauncher.iss`, compilado por `tools/build-installer.ps1` (publica o exe e chama o `ISCC` apontando para o publish, para nunca empacotar um binário velho). Saída: ~25 MB, contra os 75 MB do exe self-contained.

**Restrições que vêm do desenho, não do gosto:**

- `PrivilegesRequired=lowest`. A instalação não pode pedir UAC — instalador elevado faria o atalho e o "executar ao final" herdarem integridade alta, e o Discord rodaria como administrador. É o mesmo motivo do `asInvoker` no manifest.
- `AppId` fixo, senão cada versão vira uma entrada nova em "Aplicativos instalados".
- `[UninstallDelete]` precisa apagar `%LocalAppData%\DiscordVpnLauncher` explicitamente: os binários extraídos e os logs ficam fora de `{app}`.

**Desativar o auto-start são dois lugares, e um sem o outro não resolve:** `OPEN_ON_STARTUP` no `%AppData%\discord\settings.json` (fonte da verdade — o Discord recria a entrada do registro a partir dela) e a chave `Run` do `HKCU` (que continuaria valendo até o Discord reabrir). O Discord também precisa estar fechado, porque reescreve o `settings.json` a partir da memória ao sair — daí a confirmação "fechar o Discord agora?".

Essa lógica vive em [installer/desativar-autostart.ps1](installer/desativar-autostart.ps1), extraído para o `{tmp}` na instalação (`Flags: dontcopy`), e **não** no `[Code]` Pascal: o `LoadStringFromFile` do Inno só lê `AnsiString`, e reescrever um JSON UTF-8 por ali corromperia acentos na configuração do usuário. O script também roda sozinho, para quem quiser aplicar depois.

Validado com o `settings.json` real (que **não** tinha a chave `OPEN_ON_STARTUP`, o caminho de inserção), mais os casos com a chave `true`, sem espaço e arquivo `{}` vazio: JSON continua parseável, acentos preservados, sem BOM (o Discord lê com `JSON.parse`, que engasga com BOM). O primeiro teste pegou um bug que teria corrompido a configuração de todo mundo: `Regex::Replace(s, p, r, 1)` — o quarto parâmetro do método **estático** é `RegexOptions`, não contagem, então a chave era injetada em *todo* `{` do arquivo, inclusive dentro de `WINDOW_BOUNDS`.

O setup não é assinado: SmartScreen avisa na primeira execução. Resolver exigiria certificado de code signing pago — aceito, como já era para o `.exe`.

### 15.10 A localização do Discord se pergunta ao Windows, não ao usuário

O plano assumia `%LocalAppData%\Discord` com escape por variável de ambiente. Isso não cobre quem instalou o Discord em outro HD ou pasta personalizada — e a variável de ambiente é inviável como resposta para quem só recebeu o `.exe` de um amigo.

A tentação é pedir o caminho no instalador. Não resolve: na hora de instalar a pessoa raramente sabe o que apontar, e o valor **congela** — reinstalou o Discord em outro lugar, o campo passa a mentir e o launcher quebra sem explicar por quê.

O Discord já responde essa pergunta, em dois lugares que ele escreve onde quer que esteja instalado:

```
HKCU\...\Uninstall\Discord\InstallLocation       -> C:\...\Discord
HKCU\Software\Classes\discord\shell\open\command -> "C:\...\Discord\app-1.0.9254\Discord.exe" --url -- "%1"
```

`DiscordController.LocalizarLauncher` passou a tentar, em ordem: env var → escolha manual lembrada → registro → caminho padrão → **perguntar** (seletor de arquivo nativo, `GetOpenFileNameW`, com a resposta guardada em `discord-path.txt`). O registro vem antes do caminho padrão de propósito: é a fonte sempre atual.

A escolha lembrada mora na raiz da pasta de dados, **não** em `work\` — essa é limpa a cada sessão. E some sozinha se o arquivo apontado deixar de existir, para voltar a detectar em vez de insistir num caminho morto.

`Montar` deduz a variante pelo nome da pasta (`DiscordPTB\Update.exe` → `--processStart DiscordPTB.exe`): passar `Discord.exe` fixo faria o stub do PTB tentar abrir um app inexistente.

Validado com as duas fontes isoladas uma da outra (backup e restauração do registro real), apontando para uma instalação falsa fora do `%LocalAppData%`: as duas acharam, e a variante saiu correta.

Junto veio `--diagnostico`, que imprime o alvo detectado e o estado dos binários sem tocar em nada — pensado para suporte a distância, já que a ferramenta agora roda em máquinas que não são a do autor.

### 16.1 A desinstalação tem que desfazer o que mexeu fora de casa

Teste de desinstalação da 1.2.0: tudo o que o instalador criou saiu — pasta do programa, os dois atalhos, entrada em "Aplicativos instalados" e `%LocalAppData%\DiscordVpnLauncher` com binários e logs. Mas duas coisas ficaram:

- **a inicialização automática do Discord continuou desativada**;
- o backup `settings.json.bak-vpnlauncher` permaneceu na pasta do Discord.

A primeira é a que importa. Apagar os próprios arquivos é limpeza; manter uma alteração na configuração de **outro programa** depois de sumir da máquina é deixar rastro em software de terceiros — o usuário fica com um Discord que não abre mais junto com o Windows e sem nada que ligue uma coisa à outra, porque o culpado já foi desinstalado.

A desinstalação passou a **perguntar** ("Deseja reativá-la agora?"). Decidir sozinho seria errado nos dois sentidos: religar por conta própria contraria quem gostou do resultado; não oferecer nada contraria quem só queria testar a ferramenta.

Implementação, com os detalhes que não são óbvios:

- O `desativar-autostart.ps1` deixou de ser `dontcopy` e passa a ser **instalado em `{app}`**: a desinstalação precisa dele em disco no momento em que roda. Roda em `usUninstall`, antes da remoção dos arquivos.
- `-Reativar` devolve `OPEN_ON_STARTUP` para `true` **e** recria a entrada `Run`, localizando o `Update.exe` pelas mesmas fontes do launcher (funciona com Discord em outro HD). Só o JSON não bastaria: o auto-start só voltaria depois de o Discord ser aberto uma vez.
- `-LimparBackup` vai sempre, mesmo com resposta "não" — o `.bak` é lixo nosso.
- A pergunta só aparece se **nós** desativamos: `RegisterPreviousData` grava um marcador na chave de desinstalação. Sem ele, quem instalou sem marcar a caixa receberia uma pergunta que afirma algo falso, e um "sim" distraído ligaria um auto-start mantido desligado de propósito.

**Ao ler o marcador, o nome leva o prefixo `Inno Setup CodeFile: `** — o `SetPreviousData` o acrescenta. Ler pelo nome cru devolve sempre falso, e a falha é **silenciosa**: o desinstalador simplesmente não pergunta nada. Só apareceu porque o teste conferia o registro em vez de confiar na tela.

Validado nos dois caminhos, com o estado real da máquina restaurado ao original antes de começar: instalação **sem** a caixa → desinstalação não toca no auto-start; instalação **com** a caixa → desativa e grava o marcador, e a desinstalação restaura a entrada `Run` idêntica à original (inclusive `--process-start-args "--start-inactive"`), com o `settings.json` continuando válido e sem o `.bak`.

### 15.11 O ping da call segue o IP registrado, não o túnel (seção 6, passo 5)

Sessão de 18/08/2026: o launcher rodou limpo — `connected:JP` às 23:00:13, IP confirmado fora do BR, sessão do Discord firmada pelo túnel, teardown completo às 23:00:40 (`openvpn.exe encerrado`, `Adaptador 'DiscordVpnLauncher' removido`). Um minuto depois, já sem VPN nenhuma, a call marcava **304 ms**.

Não era vazamento. Conferido na máquina com o script encerrado: nenhum `openvpn.exe`, nenhum adaptador wintun, uma única rota padrão pelo gateway de casa, IP público `177.33.26.19` (Claro/SP, `BR`) — e o painel de transporte do Discord mostrando esse mesmo IP como *Local Address*. A rede voltou ao normal exatamente como projetado.

**O que não volta é a região de voz.** O Discord escolhe o servidor de mídia a partir do IP registrado no login, e essa escolha sobrevive ao teardown junto com o registro que a ferramenta existe para produzir. Com um relay japonês, o usuário fica falando com um servidor no Japão pela internet brasileira: ~300 ms, permanentes até o próximo login.

Ou seja, a escolha do relay não é indiferente, como o plano original assumia ao ranquear só por `Score`. O país do relay é o país da região de voz da sessão inteira. E o ranking por `Score` puro entregava sempre o pior caso possível: a lista do VPNGate é dominada pelo Japão (na medição, 52 de 97 relays JP e 27 KR), então os 5 candidatos saíam `JP, JP, JP, JP, JP` — o ponto mais distante do Brasil no mapa.

A seleção passou a ordenar por **distância até o Brasil** (`PaisesPorProximidade`, da América do Sul ao Extremo Oriente), com `Score`/`Ping` desempatando dentro do mesmo país. Um relay no Chile registra um IP tão `!= BR` quanto um japonês — a diferença é o ping que fica depois.

Duas decisões que sustentam isso:

- **O último slot fica reservado ao melhor `Score` da lista inteira.** Priorizar proximidade sem rede de segurança troca "ping ruim" por "sessão falhou": se a vizinhança do Brasil só tiver relays moribundos, o retry do broker gasta os 5 candidatos e cai no popup. Melhor subir longe do que não subir.
- **A latência do próprio túnel não entra na conta.** Ele vive menos de um minuto e só precisa carregar o login; o que se otimiza aqui é o que sobra *depois* dele.

Efeito medido sobre a mesma lista de 97 relays: `JP, JP, JP, JP, JP` virou `US, UA, BY, RU, JP` — o `US` (score 1,9M, ping 3 ms) na frente, o `JP` de maior score guardado como reserva no fim.

---

### 15.12 Quatro ajustes de fluxo pedidos em uso real (seções 6 e 9)

Depois de rodar o launcher no dia a dia, quatro pontos do fluxo mudaram. Nenhum deles altera a sacada central (IP fotografado uma vez, túnel curto), mas três deles mexem em tempo — e tempo aqui é ping cobrado do usuário.

**1. Kill do Discord com zero processos vira no-op (passo 3).** O passo esperava até 5 s pelo `discord-ipc-0` sumir depois do kill. Só que o pipe é do processo do Discord e morre junto com ele: sem Discord aberto, não existe pipe órfão a aguardar. E "sem Discord aberto" é justamente o caso comum de quem seguiu o pré-requisito e desativou o início automático. A espera saiu quando `MatarTudo()` devolve `0`; com 1+ processos encerrados ela continua, porque aí o pipe pode mesmo demorar a cair.

**2. `US` na frente da ordem de conexão (passo 5).** 15.11 ordenou por distância até o Brasil, e na prática a vizinhança sul-americana entrega pouco: são um ou dois relays, quase sempre fora do ar, e cada um deles gasta uma tentativa de 20 s do broker antes de a sessão chegar em algo que conecta. Os EUA são o meio-termo real — ~120 ms de US East contra ~300 ms do Japão, com a única oferta grande e estável de relays da lista. `US` passou a ser o primeiro item de `PaisesPorProximidade`; o resto da ordem por distância ficou como estava.

Junto veio um teto de **3 candidatos por país** (`MaximoPorPais`): com 15 relays US na lista, sem teto os 5 slots sairiam todos dos EUA e o retry deixaria de ser retry — uma rota ruim até o país derrubaria a sessão sem que nenhuma alternativa tivesse sido tentada. A seleção faz duas passadas sobre a mesma ordem: a primeira respeita o teto, a segunda preenche as vagas que sobraram quando não há países suficientes (lista só-JP continua rendendo 5 candidatos JP). Sobre uma lista sintética no formato da real (52 JP, 15 US, 8 KR, 1 UY, 1 CL), o resultado é `US, US, US, UY, JP` — preferência, variedade de retry e a reserva de maior `Score` no fim.

**3. Estabilização entre conectar e abrir o Discord (passo 10b).** `Initialization Sequence Completed` mais uma confirmação de país provam que o túnel *chegou* a subir — não que ele continua de pé no segundo seguinte. Relay gratuito que cai nos primeiros segundos, rota que ainda oscila e adaptador que perde o IPv4 acontecem depois desse ponto, e o `connected` do broker não volta atrás quando acontecem. Lançar o Discord em cima disso é o pior cenário possível: ele registra o IP real e não há segunda chance sem matar e relançar.

Agora há um respiro fixo e então duas checagens seguidas (adaptador com IPv4 + país `!= BR`, 2 s entre elas). Uma falha isolada **não** condena a sessão — oscilar logo após a troca de rotas é normal, então o contador zera e a janela de 30 s continua correndo. Não estabilizar dentro dela, sim, é falha: melhor o popup com as duas saídas do que um Discord aberto por um túnel que não existe mais.

**4. O teardown virou um botão (passo 12b).** `EsperarCapturaDeIp` acerta o caso comum, mas é heurística de rede: ela não vê uma tela de login, um update do Discord em andamento nem um 2FA esperando o celular do usuário. O popup de OK entrega essa decisão a quem está olhando a tela.

O clique não pode ser a **única** saída — "o túnel nunca fica aberto" não pode virar refém de um popup ignorado (usuário saiu, tela bloqueada). Por isso o dialogo vive em uma thread própria e, estourado o teto de 10 min, é dispensado de fora com `WM_COMMAND`/`IDOK` (mais `WM_CLOSE` como reserva, insistindo por 5 s). E mesmo que a janela insista em ficar na tela, o teardown acontece: a thread é de background e não segura a saída do processo.

Armadilha registrada: `FindWindow` precisa de um título **próprio** para achar esse dialogo. O `Console.Title` do launcher é `"Discord VPN Launcher"`, o mesmo caption dos outros popups — buscar por ele acertaria a janela errada e deixaria o dialogo (e o túnel) de pé. Daí o `"Discord VPN Launcher - VPN ligada"`, com a busca restrita à classe `#32770` dos dialogos.
