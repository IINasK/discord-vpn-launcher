# Plano de Ação — Discord VPN Launcher (C#)

## Objetivo

Um único `.exe` que o usuário abre manualmente. Ele sobe uma VPN gratuita em um servidor **fora do Brasil**, lança o Discord por baixo dessa VPN (para o Discord "fotografar" o IP não-brasileiro na inicialização), e **derruba a VPN** assim que o Discord confirma que subiu. Discord roda **sem privilégio de administrador**.

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

**Consequência de design:** todo o retry de relay acontece *dentro do broker*, para manter **1 UAC só**. Por isso o pai entrega uma **lista de candidatos** (ex.: os 5 melhores `!= BR`), não um único config.

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
3. **Matar Discord aberto.** Encerrar **todos** os processos `Discord.exe` (ele roda em vários). Sem isso, relançar só foca a janela existente → sem re-captura de IP.
4. **Baixar lista VPNGate.** `GET https://www.vpngate.net/api/iphone/` (CSV). Sem login, sem chave.
5. **Filtrar e ranquear.** Descartar linhas com `CountryShort == "BR"`. Ordenar por `Score`/uptime (ou menor `Ping`). Pegar os **top N (≈5)** candidatos.
6. **Se lista `!= BR` vazia** → popup de falha (raro, mas trate).
7. **Decodificar configs.** Cada candidato tem o `.ovpn` inteiro em base64 na última coluna (`OpenVPN_ConfigData_Base64`), com CA/cert/key inline — self-contained, sem arquivos extras. Decodificar e gravar `work\cand1.ovpn`, `cand2.ovpn`, ...
8. **Subir o broker (1 UAC).** Relançar a si mesmo elevado: `exe --broker <workDir> <pidDoPai>`. O `runas` dispara **um** UAC.
9. **Aguardar conexão.** Poll em `work\vpn-status.txt` (escrito pelo broker):
   - `connected:<país>` → seguir.
   - `failed:all` ou **timeout** (ex.: 45 s) → popup de falha.
10. **Confirmar país (ipinfo).** `GET https://ipinfo.io/country`. Se `!= BR` → ok. Se `== BR` (label do VPNGate mentiu) → tratar como falha → popup. *(Passo simples e único; não entra no loop de retry, para não complicar.)*
11. **Lançar Discord (não-elevado).** Via stub do Squirrel: `%LocalAppData%\Discord\Update.exe --processStart Discord.exe` (aponta sempre para a versão atual). `UseShellExecute` normal → herda integridade média do pai.
12. **Esperar Discord pronto.** Poll pela existência de `\\.\pipe\discord-ipc-0` (timeout ≈ 30 s). Apareceu = inicializou.
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
