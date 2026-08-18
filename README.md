# Discord VPN Launcher

Um único `.exe` que sobe uma VPN gratuita em um servidor **fora do Brasil**, abre o Discord por baixo dessa VPN (para o Discord registrar o IP não-brasileiro na inicialização) e **derruba a VPN** assim que o Discord termina de se registrar. O Discord roda **sem privilégio de administrador**.

Ao derrubar a VPN, o Discord reconecta pelo seu IP real brasileiro e **continua funcionando normalmente**, mantendo o IP não-brasileiro que já registrou. É isso que permite usar a VPN só por ~1 minuto, no lugar de deixá-la ligada o tempo todo: sua conexão volta ao normal logo depois.

O plano de arquitetura completo está em [plano-discord-vpn-launcher.md](plano-discord-vpn-launcher.md).

---

## ⚠️ Pré-requisito obrigatório: desativar o auto-start do Discord

O Discord se auto-inicia no login do Windows por padrão. Se isso ficar ligado, **ele abre pelo seu IP real antes do launcher e todo o propósito da ferramenta é perdido**.

Faça uma vez:

1. Discord → **Configurações do Usuário** → **Configurações do Windows**
2. Desligue **"Abrir o Discord"** (*Open Discord* / iniciar com o sistema)
3. Confira também em **Gerenciador de Tarefas → Inicializar** que não sobrou entrada do Discord

O launcher **não** faz isso automaticamente — mexer no startup/registro do usuário está fora do escopo dele.

---

## Como funciona

```
Discord.exe                       (orquestrador, integridade média, SEM UAC)
 │
 ├─ extrai openvpn.exe + wintun.dll para %LocalAppData%\DiscordVpnLauncher\bin
 ├─ confere internet e o país atual (ipinfo.io/country)
 ├─ mata todos os processos do Discord
 ├─ baixa a lista do VPNGate, descarta os relays BR, grava os 5 melhores em .ovpn
 ├─ relança a si mesmo elevado: --broker …          ← ÚNICO prompt de UAC
 │     └─ broker (integridade alta)
 │          ├─ cria o adaptador wintun pelo wintun.dll (P/Invoke)
 │          ├─ tenta candidato 1, 2, 3… até um subir   ← retry aqui, sem novo UAC
 │          ├─ sinal de sucesso: "Initialization Sequence Completed" no log
 │          └─ vigia o PID do pai e o arquivo stop.signal
 │
 ├─ confirma que o IP realmente não é BR
 ├─ lança o Discord NÃO-elevado (herda integridade média)
 ├─ espera o pipe \\.\pipe\discord-ipc-0 aparecer (o processo subiu)
 ├─ espera uma conexão do Discord pelo IP do túnel SOBREVIVER 6 s (sessão do
 │     gateway firmada = login concluído = IP registrado), + 5 s de margem
 └─ escreve stop.signal → o broker derruba o túnel, remove o adaptador
                          e restaura as rotas
```

**Por que existe um "broker" elevado em vez de só rodar o OpenVPN como admin:** um processo de integridade média não consegue matar um processo de integridade alta. Se o filho elevado fosse o próprio `openvpn.exe`, o launcher não conseguiria derrubá-lo no fim e o túnel ficaria aberto. O broker elevado é dono do processo do OpenVPN, então ele consegue encerrá-lo.

O túnel nunca fica aberto: há dois mecanismos redundantes — o `stop.signal` escrito no `finally` do pai **e** o watchdog do broker sobre o PID do pai.

---

## Compilar

### 1. Requisitos

- **.NET SDK 8** — `winget install Microsoft.DotNet.SDK.8`
- **Binários do OpenVPN 2.6+** (não versionados neste repositório)

### 2. Baixar os binários do OpenVPN

```powershell
powershell -ExecutionPolicy Bypass -File tools\get-openvpn-binaries.ps1
```

O script **não instala nada** nesta máquina — só baixa e extrai para `DiscordVpnLauncher/Resources/`, verificando a assinatura Authenticode de cada arquivo:

| Arquivo | Origem | Assinado por |
|---|---|---|
| `openvpn.exe` | MSI oficial do OpenVPN 2.6.14, extraído com `msiexec /a` | OpenVPN Inc. |
| `libcrypto-3-x64.dll`, `libssl-3-x64.dll`, `libpkcs11-helper-1.dll`, `vcruntime140.dll` | mesmo MSI | OpenVPN Inc. |
| `wintun.dll` | release oficial do [wintun.net](https://www.wintun.net) (projeto WireGuard) | WireGuard LLC |

Três detalhes que o plano original não previa (registrados na seção 15 dele):

- **`openvpn.exe` não roda sozinho.** Sem as DLLs do OpenSSL ele morre com `0xC0000135` (*DLL not found*) antes de imprimir qualquer coisa. Por isso o conjunto tem 6 arquivos, não 2.
- **O MSI do OpenVPN não contém `wintun.dll`** (verificado, inclusive com `ADDLOCAL=ALL`) — o instalador trata o driver por outro caminho. O `wintun.dll` vem do upstream, que é a origem do arquivo de qualquer forma.
- **O `openvpn.exe` não cria o adaptador de rede.** Sem um adaptador pronto ele completa o TLS, recebe o `PUSH_REPLY` e morre com *"There are no TAP-Windows, Wintun or ovpn-dco adapters on this system"*. Por isso o broker cria o adaptador antes de conectar (chamando o `wintun.dll` direto, que instala o driver embutido sob demanda) e o remove no teardown.

Tudo isso entra no `.exe` como `EmbeddedResource` e é extraído para `%LocalAppData%\DiscordVpnLauncher\bin\` no 1º uso — sem instalador e sem download externo em runtime. O OpenVPN 2.6 usa **wintun** por padrão, o que evita o driver TAP clássico.

O projeto **compila sem esses arquivos** (emite apenas um aviso), mas o launcher falha em runtime com mensagem explícita. Eles estão no `.gitignore` por serem binários de terceiros, pesados e reproduzíveis pelo script.

> Fique na série **2.6.x** do OpenVPN. A 2.7 removeu mais opções legadas e não foi validada com os configs do VPNGate.

### 3. Publicar

```powershell
dotnet publish DiscordVpnLauncher -c Release
```

Saída: `DiscordVpnLauncher/bin/Release/net8.0-windows/win-x64/publish/Discord.exe` — self-contained e single-file (~70 MB, com o runtime .NET dentro), com o ícone do Discord.

### 4. Gerar o instalador (opcional)

Para distribuir para outras pessoas, em vez de mandar o `.exe` solto:

```powershell
winget install JRSoftware.InnoSetup          # uma vez
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
```

Saída: `installer\Output\DiscordVpnLauncher-Setup-<versão>.exe` (~25 MB — o Inno comprime o self-contained de 75 MB). O instalador:

- **não pede UAC** (`PrivilegesRequired=lowest`) — instala em `%LocalAppData%\Programs\DiscordVpnLauncher`;
- cria atalho **"Discord"** no Menu Iniciar e, opcionalmente, na área de trabalho;
- oferece **desativar o início automático do Discord** (o pré-requisito acima), ajustando `settings.json` e a chave `Run` do registro;
- registra desinstalador em "Aplicativos instalados", que remove também `%LocalAppData%\DiscordVpnLauncher` (binários do OpenVPN e logs).

> O setup **não é assinado**: o SmartScreen avisa na primeira execução (*Mais informações → Executar assim mesmo*). Resolver isso exigiria um certificado de code signing pago.

---

## Usar

Abra o `.exe` manualmente. Não há inicialização automática nem tarefa agendada, por decisão de projeto.

- Aceite **um** prompt de UAC (é o broker da VPN). O Discord **não** deve gerar UAC.
- Em caso de falha aparece um popup **"Erro ao conectar em outro país"** com duas saídas:
  - **Sim** → continuar sem VPN (abre o Discord no seu IP real brasileiro)
  - **Não** → fechar sem abrir o Discord

### Discord em outro HD ou pasta personalizada

**Não precisa configurar nada.** O launcher pergunta ao Windows onde o Discord está, nesta ordem:

1. a variável `DISCORD_VPN_LAUNCHER_DISCORD`, se você quiser forçar um caminho;
2. uma escolha manual lembrada de uma execução anterior;
3. o **registro** — a entrada de desinstalação (`InstallLocation`) e o handler do protocolo `discord://`, que o Discord escreve onde quer que tenha sido instalado;
4. o caminho padrão `%LocalAppData%\Discord` (e PTB/Canary);
5. se nada disso resolver, ele **pergunta**: um seletor de arquivo abre para você apontar o `Discord.exe` (ou o `Update.exe`), e a resposta fica guardada para as próximas vezes.

Ou seja, instalação em `D:\Jogos\Discord` funciona sozinha — o passo 3 acha. O passo 5 só aparece em caso realmente exótico, como uma cópia portátil que nunca foi instalada.

Para conferir o que ele detectou, sem abrir nada:

```powershell
.\Discord.exe --diagnostico
```

```
  Discord em    : C:\Users\voce\AppData\Local\Discord\Update.exe --processStart Discord.exe
  openvpn.exe   : extraido
```

Se alguém relatar que "não abre", **é o primeiro comando a pedir**.

### Quanto tempo a VPN fica de pé (e por que isso importa para call)

**Enquanto o túnel está ativo, todo o seu tráfego passa pelo relay no exterior — o ping em call fica impraticável.** Por isso a janela é a menor possível: o launcher não espera um tempo fixo, ele observa o Discord.

A VPN cai assim que uma conexão do Discord pelo túnel **sobrevive 6 s** (é a sessão do gateway; as conexões de boot morrem em menos de 1 s), mais 5 s de margem. Na prática são ~15 s de VPN depois que o Discord abre, contra os 30 s fixos da versão anterior. O console mostra a contagem regressiva — **espere ela terminar antes de entrar em call**; a última linha é `VPN desligada, ping normal`.

Se algum dia o IP registrado sair como brasileiro, aumente a margem:

```powershell
$env:DISCORD_VPN_LAUNCHER_ESPERA = "20"   # segundos, máximo 300
```

---

## Diagnóstico

Tudo fica em `%LocalAppData%\DiscordVpnLauncher\`:

| Caminho | Conteúdo |
|---|---|
| `bin\` | `openvpn.exe` e `wintun.dll` extraídos (reaproveitados entre sessões) |
| `work\broker.log` | log do broker — a janela dele é oculta, então é aqui que o diagnóstico do túnel aparece |
| `work\launcher.log` | log do processo pai — falhas depois do túnel subir (país, Discord) aparecem aqui |
| `work\openvpn.log` | log do OpenVPN do candidato ativo |
| `work\openvpn-candN-falhou.log` | log preservado de cada candidato que não subiu |
| `work\vpn-status.txt` | canal de status do broker para o pai (`connected:XX`, `failed:all`, …) |

Os `cand*.ovpn` são apagados no teardown porque contêm chave privada inline. Os logs não têm segredo e ficam para análise.

Para ver o log de uma execução com problema:

```powershell
Get-Content "$env:LOCALAPPDATA\DiscordVpnLauncher\work\broker.log" -Tail 40
```

---

## Validação

Já verificado automaticamente (sem UAC, sem tocar no Discord):

- [x] Extração dos 6 binários embutidos para uma pasta limpa
- [x] `openvpn.exe` executa (dependências resolvidas)
- [x] Lista do VPNGate baixa e parseia; nenhum candidato `BR`; ordenação por `Score`
- [x] Configs decodificados com chave inline, país anotado e sem opções que o 2.6 rejeita
- [x] O OpenVPN aceita o config sanitizado: completa TLS e recebe `PUSH_REPLY`
- [x] Manifest do `.exe` publicado é `asInvoker`

Falta a bateria que exige interação com o UAC (seção 12 do plano):

- [ ] **1 UAC só** — do início ao fim deve aparecer **um** prompt (o broker) e nenhum no Discord
- [ ] **Discord não-elevado** — arrastar um arquivo para a janela dele tem que funcionar
- [ ] **País** — durante a janela conectada, `ipinfo.io/country` retorna `!= BR`
- [ ] **Fim-a-fim** — o estado capturado pelo Discord persiste depois da VPN cair
- [ ] **Cleanup em crash** — matar o launcher à força no meio; o broker derruba o túnel e o adaptador sozinho (confira com `ipconfig` que o adaptador `DiscordVpnLauncher` sumiu)
- [ ] **Caminho de falha** — desconecte a rede: o popup aparece, "Sim" abre sem VPN, "Não" encerra limpo

## Limitações conhecidas

- **Relays do VPNGate são instáveis.** O retry cobre 5 candidatos; em dia ruim a sessão cai no popup de falha — comportamento esperado.
- **SmartScreen / antivírus.** Um `.exe` self-extracting que solta o `openvpn.exe` e altera rotas de rede costuma ser sinalizado. Para uso próprio, basta liberar; para distribuir, seria necessário assinatura de código.
- **A premissa central** é que o Discord "fotografa" o IP uma única vez, na inicialização — e a reconexão que acontece quando a VPN cai **não** substitui esse registro (confirmado em uso). Todo o valor da ferramenta depende disso: é o que torna suficiente uma janela curta de VPN cobrindo só o login.
- **Não rode o launcher como administrador.** Ele avisa se isso acontecer: o Discord herdaria integridade alta e passaria a rodar como admin (arrastar-e-soltar arquivos para a janela, por exemplo, deixa de funcionar).
