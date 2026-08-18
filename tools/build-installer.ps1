# Gera o instalador (setup.exe) do Discord VPN Launcher.
#
# Faz o publish e chama o compilador do Inno Setup apontando para o exe recem
# publicado - assim o instalador nunca embala um binario velho por engano.
#
# Requer o Inno Setup 6:  winget install JRSoftware.InnoSetup

[CmdletBinding()]
param(
    # Pula o dotnet publish e usa o que ja estiver publicado.
    [switch] $SemPublicar
)

$ErrorActionPreference = "Stop"

$raiz = Split-Path -Parent $PSScriptRoot
$projeto = Join-Path $raiz "DiscordVpnLauncher"
$publicado = Join-Path $projeto "bin\Release\net8.0-windows\win-x64\publish\Discord.exe"
$script = Join-Path $raiz "installer\DiscordVpnLauncher.iss"

function Find-ISCC {
    $comando = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($comando) { return $comando.Source }

    $candidatos = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($caminho in $candidatos) {
        if ($caminho -and (Test-Path $caminho)) { return $caminho }
    }

    throw "ISCC.exe (Inno Setup 6) nao encontrado. Instale com: winget install JRSoftware.InnoSetup"
}

if (-not $SemPublicar) {
    Write-Host "==> Publicando o launcher..." -ForegroundColor Cyan
    dotnet publish $projeto -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou." }
}

if (-not (Test-Path $publicado)) {
    throw "Executavel publicado nao encontrado em $publicado. Rode sem -SemPublicar."
}

# Aviso util: sem os binarios do OpenVPN embutidos o instalador gera um launcher
# que falha em runtime, e isso so apareceria na maquina do usuario final.
$recursos = Get-ChildItem (Join-Path $projeto "Resources") -File -ErrorAction SilentlyContinue
if (-not $recursos -or -not ($recursos.Name -contains "openvpn.exe")) {
    Write-Warning "Resources\openvpn.exe ausente: o instalador vai empacotar um launcher que NAO sobe VPN."
    Write-Warning "Rode tools\get-openvpn-binaries.ps1 e refaca o build."
}

$iscc = Find-ISCC
Write-Host "==> Compilando o instalador com $iscc" -ForegroundColor Cyan

& $iscc "/DFonteExe=$publicado" $script
if ($LASTEXITCODE -ne 0) { throw "ISCC falhou (codigo $LASTEXITCODE)." }

$saida = Get-ChildItem (Join-Path $raiz "installer\Output") -Filter "*.exe" |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1

Write-Host ""
Write-Host "Instalador pronto:" -ForegroundColor Green
Write-Host "  $($saida.FullName)"
Write-Host "  $([math]::Round($saida.Length / 1MB, 1)) MB"
Write-Host ""
Write-Host "Ele nao e assinado: o SmartScreen vai avisar na primeira execucao" -ForegroundColor Yellow
Write-Host "(Mais informacoes -> Executar assim mesmo)." -ForegroundColor Yellow
