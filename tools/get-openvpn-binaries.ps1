<#
.SYNOPSIS
    Obtem openvpn.exe e wintun.dll para embutir no DiscordVpnLauncher.

.DESCRIPTION
    Nada e instalado nesta maquina: o script apenas baixa e extrai.

      openvpn.exe  MSI oficial do OpenVPN Community, extraido com "msiexec /a"
                   (administrative install = so extrai, nao instala nem registra
                   driver de rede).

      wintun.dll   Release oficial do proprio projeto Wintun (wintun.net, do time
                   do WireGuard). O MSI do OpenVPN NAO carrega esse arquivo - o
                   instalador dele trata o driver separadamente -, por isso ele vem
                   do upstream. E o mesmo binario, assinado pela WireGuard LLC.

    A DLL embute o driver assinado e o instala sob demanda ao criar o adaptador;
    e por isso que basta o wintun.dll ao lado do openvpn.exe, sem instalar driver
    previamente. Criar o adaptador continua exigindo elevacao - dai o broker.

    Mantenha a serie 2.6.x do OpenVPN: ela usa wintun por padrao e ainda aceita as
    opcoes legadas dos configs do VPNGate. A 2.7 removeu mais opcoes antigas e nao
    foi validada com este launcher.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\get-openvpn-binaries.ps1
#>
[CmdletBinding()]
param(
    [string]$OpenVpnVersion = "2.6.14",
    [string]$OpenVpnBuild   = "I004",
    [string]$WintunVersion  = "0.14.1",
    [string]$Destino
)

$ErrorActionPreference = "Stop"

if (-not $Destino) {
    # $PSScriptRoot nao esta disponivel na secao param() em todas as versoes do
    # PowerShell, por isso o default e resolvido aqui.
    $raiz = Split-Path -Parent $MyInvocation.MyCommand.Path
    $Destino = Join-Path $raiz "..\DiscordVpnLauncher\Resources"
}

$temp = Join-Path ([IO.Path]::GetTempPath()) "dvl-binarios"
New-Item -ItemType Directory -Force -Path $temp, $Destino | Out-Null

function Assert-Assinatura($caminho, $esperado) {
    $sig = Get-AuthenticodeSignature $caminho
    if ($sig.Status -ne "Valid") {
        throw "Assinatura invalida em $(Split-Path -Leaf $caminho): $($sig.Status)"
    }
    if ($sig.SignerCertificate.Subject -notmatch $esperado) {
        throw "Assinante inesperado em $(Split-Path -Leaf $caminho): $($sig.SignerCertificate.Subject)"
    }
    Write-Host "    assinatura ok - $($sig.SignerCertificate.Subject -replace ',.*','')"
}

function Publicar($origem, $nome) {
    $alvo = Join-Path $Destino $nome
    Copy-Item $origem $alvo -Force
    $item = Get-Item $alvo
    Write-Host ("    {0,-12} {1,9:N0} bytes  v{2}" -f $nome, $item.Length, $item.VersionInfo.FileVersion)
}

# ---------------------------------------------------------------- openvpn.exe
$msiNome = "OpenVPN-$OpenVpnVersion-$OpenVpnBuild-amd64.msi"
$msi     = Join-Path $temp $msiNome
$extraido = Join-Path $temp "openvpn"

Write-Host "[1/2] openvpn.exe ($OpenVpnVersion-$OpenVpnBuild)"
Invoke-WebRequest -Uri "https://swupdate.openvpn.org/community/releases/$msiNome" -OutFile $msi -UseBasicParsing

# Assinatura de arquivo MSI (OLE compound document): guarda contra pagina de erro
# salva como .msi.
if (Compare-Object ([IO.File]::ReadAllBytes($msi)[0..7]) @(0xD0,0xCF,0x11,0xE0,0xA1,0xB1,0x1A,0xE1)) {
    throw "O download nao e um MSI valido: $msi"
}
Assert-Assinatura $msi "OpenVPN"

Remove-Item $extraido -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $extraido | Out-Null
$proc = Start-Process msiexec.exe -Wait -PassThru -ArgumentList @(
    "/a", "`"$msi`"", "/qn", "TARGETDIR=`"$extraido`"")
if ($proc.ExitCode -ne 0) {
    throw "msiexec falhou com codigo $($proc.ExitCode)."
}

$openvpn = Get-ChildItem $extraido -Filter "openvpn.exe" -Recurse -File | Select-Object -First 1
if (-not $openvpn) { throw "openvpn.exe nao encontrado no MSI extraido." }
Assert-Assinatura $openvpn.FullName "OpenVPN"
Publicar $openvpn.FullName "openvpn.exe"

# openvpn.exe nao roda sozinho: sem estas DLLs ele morre com 0xC0000135
# (DLL_NOT_FOUND) antes de imprimir qualquer coisa. Todas saem do mesmo bin\ do MSI.
#
# O tapctl.exe do MSI NAO e usado: ele cria adaptadores via SETUPAPI, o que exige o
# driver ja no driver store da maquina. Quem cria o adaptador aqui e o proprio
# wintun.dll (ver WintunAdapter.cs), que instala o driver embutido sob demanda.
foreach ($nome in @("libcrypto-3-x64.dll", "libssl-3-x64.dll",
                    "libpkcs11-helper-1.dll", "vcruntime140.dll")) {
    $origem = Get-ChildItem $extraido -Filter $nome -Recurse -File | Select-Object -First 1
    if (-not $origem) { throw "$nome nao encontrado no MSI extraido." }
    Publicar $origem.FullName $nome
}

# ---------------------------------------------------------------- wintun.dll
$zip     = Join-Path $temp "wintun-$WintunVersion.zip"
$zipDir  = Join-Path $temp "wintun"

Write-Host "[2/2] wintun.dll ($WintunVersion)"
Invoke-WebRequest -Uri "https://www.wintun.net/builds/wintun-$WintunVersion.zip" -OutFile $zip -UseBasicParsing

Remove-Item $zipDir -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -Path $zip -DestinationPath $zipDir -Force

# O zip traz uma DLL por arquitetura; o launcher e win-x64.
$wintun = Join-Path $zipDir "wintun\bin\amd64\wintun.dll"
if (-not (Test-Path $wintun)) {
    $wintun = (Get-ChildItem $zipDir -Filter "wintun.dll" -Recurse -File |
               Where-Object { $_.DirectoryName -match 'amd64|x64' } |
               Select-Object -First 1).FullName
}
if (-not $wintun) { throw "wintun.dll (amd64) nao encontrado no zip." }
Assert-Assinatura $wintun "WireGuard"
Publicar $wintun "wintun.dll"

Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Binarios prontos em $((Resolve-Path $Destino).Path)"
