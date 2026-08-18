; Instalador do Discord VPN Launcher (Inno Setup 6).
;
; ESTE ARQUIVO PRECISA SER SALVO EM UTF-8 COM BOM. Sem o BOM, o Inno le o .iss
; como ANSI e todo texto acentuado que aparece na tela do instalador vira
; caractere quebrado - sem erro de compilacao, so no resultado final.
;
; Gere com tools\build-installer.ps1 - ele publica o exe e chama o ISCC com o
; caminho correto. Compilar este .iss na mao tambem funciona, desde que o
; publish ja exista.
;
; Duas decisoes que nao sao detalhe:
;
; 1. PrivilegesRequired=lowest. A instalacao NAO pode pedir UAC. Se o instalador
;    rodasse elevado, o atalho e o "executar ao final" herdariam integridade alta
;    e o Discord acabaria rodando como administrador - que e exatamente o que o
;    projeto inteiro evita (ver app.manifest / CLAUDE.md).
;
; 2. O executavel se chama Discord.exe e o atalho se chama "Discord", de proposito:
;    o usuario clica nele no lugar do Discord real. O Discord de verdade continua
;    instalado e com os atalhos dele intactos.

#define AppNome "Discord VPN Launcher"
#define AppVersao "1.2.1"
#define AppExe "Discord.exe"
#define AppAutor "Douglas"

; Sobrescrito por tools\build-installer.ps1; o default serve para compilar na mao.
#ifndef FonteExe
  #define FonteExe "..\DiscordVpnLauncher\bin\Release\net8.0-windows\win-x64\publish\Discord.exe"
#endif

[Setup]
; AppId fixo: e o que faz uma nova versao ATUALIZAR a instalacao existente em vez
; de criar uma segunda entrada em "Aplicativos instalados". Nunca mude.
AppId={{8F3C2A94-6D51-4E2B-9C77-1B5A0E3D7F42}
AppName={#AppNome}
AppVersion={#AppVersao}
AppPublisher={#AppAutor}
AppPublisherURL=https://github.com/IINasK/discord-vpn-launcher
AppSupportURL=https://github.com/IINasK/discord-vpn-launcher/issues
AppUpdatesURL=https://github.com/IINasK/discord-vpn-launcher/releases
DefaultDirName={localappdata}\Programs\DiscordVpnLauncher
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=DiscordVpnLauncher-Setup-{#AppVersao}
SetupIconFile=..\DiscordVpnLauncher\discord_icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppNome}
; O exe ja e um self-contained de ~75 MB; lzma2/max derruba isso para menos da
; metade, ao custo de alguns minutos de compilacao.
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar um atalho na Área de Trabalho"; GroupDescription: "Atalhos adicionais:"
Name: "desativarautostart"; Description: "Desativar a inicialização automática do Discord (recomendado)"; GroupDescription: "Configuração:"

[Files]
Source: "{#FonteExe}"; DestDir: "{app}"; DestName: "{#AppExe}"; Flags: ignoreversion
; dontcopy: sai para o {tmp} durante a instalacao e nao fica na maquina. O
; ajuste do settings.json vive num .ps1 porque ele e JSON em UTF-8 - o Pascal do
; Inno so le arquivo como AnsiString, e reescrever isso corromperia acentos na
; configuracao do usuario.
Source: "desativar-autostart.ps1"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\Discord"; Filename: "{app}\{#AppExe}"; Comment: "Inicia o Discord através de uma VPN fora do Brasil"
Name: "{autodesktop}\Discord"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon; Comment: "Inicia o Discord através de uma VPN fora do Brasil"

[Run]
; runasoriginaluser: garante integridade media mesmo se o instalador tiver sido
; aberto elevado por algum motivo. Sem isso o Discord poderia herdar admin.
Filename: "{app}\{#AppExe}"; Description: "Iniciar o Discord através da VPN agora"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
; Binarios do OpenVPN extraidos e logs de sessao. Nao ficam em {app}, entao o
; desinstalador precisa apaga-los explicitamente.
Type: filesandordirs; Name: "{localappdata}\DiscordVpnLauncher"

[Code]

{ Desativa o inicio automatico do Discord.

  A logica mora em desativar-autostart.ps1, nao aqui: o alvo e um JSON em UTF-8
  (settings.json do Discord) e o Pascal do Inno so le arquivo como AnsiString -
  reescrever com isso corromperia qualquer acento na configuracao do usuario.
  O .ps1 tambem serve para o usuario rodar na mao depois, se quiser. }
procedure DesativarAutoStart();
var
  Codigo: Integer;
  Argumentos: String;
begin
  ExtractTemporaryFile('desativar-autostart.ps1');

  Argumentos := '-NoProfile -ExecutionPolicy Bypass -File "' +
                ExpandConstant('{tmp}\desativar-autostart.ps1') + '"';

  { O Discord reescreve o settings.json ao fechar: com ele aberto, a alteracao
    seria desfeita no proximo fechamento. }
  if MsgBox('É necessário que o Discord esteja fechado para que esta alteração ' +
            'tenha efeito.' + #13#10 + #13#10 +
            'Deseja encerrá-lo agora?' + #13#10 + #13#10 +
            'Caso permaneça aberto, o Discord poderá restaurar a configuração ' +
            'anterior ao ser encerrado.',
            mbConfirmation, MB_YESNO) = IDYES then
    Argumentos := Argumentos + ' -FecharDiscord';

  if not Exec('powershell.exe', Argumentos, '', SW_HIDE, ewWaitUntilTerminated, Codigo) then
    Codigo := -1;

  { 2 = nenhum settings.json encontrado (Discord instalado em outro lugar, ou
    nunca aberto). A entrada do registro ainda foi removida. }
  if Codigo = 2 then
    MsgBox('Não foi possível localizar a configuração do Discord.' + #13#10 + #13#10 +
           'A entrada de inicialização automática do Windows foi removida. ' +
           'Recomenda-se verificar também em:' + #13#10 + #13#10 +
           'Discord > Configurações do Usuário > Configurações do Windows > ' +
           '"Abrir o Discord".',
           mbInformation, MB_OK)
  else if Codigo <> 0 then
    { Atencao: nenhuma linha pode COMECAR com #. O pre-processador do Inno trata a
      linha inteira como diretiva e falha com "Unknown preprocessor directive" -
      por isso os #13#10 ficam sempre no fim da linha anterior. }
    MsgBox('Não foi possível desativar a inicialização automática do Discord.' + #13#10 + #13#10 +
           'Desative manualmente em: Discord > Configurações do Usuário > ' +
           'Configurações do Windows > "Abrir o Discord".' + #13#10 + #13#10 +
           'Sem essa alteração, o Discord será iniciado com o seu IP real antes ' +
           'do launcher, anulando a finalidade do programa.',
           mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('desativarautostart') then
    DesativarAutoStart();
end;
