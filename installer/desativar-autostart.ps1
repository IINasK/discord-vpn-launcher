# Desativa o inicio automatico do Discord.
#
# Chamado pelo instalador (ver DiscordVpnLauncher.iss), mas roda sozinho tambem:
#
#   powershell -ExecutionPolicy Bypass -File desativar-autostart.ps1 -FecharDiscord
#
# Por que isto existe: se o Discord subir junto com o Windows, ele registra o IP
# real ANTES do launcher rodar, e a ferramenta inteira perde o sentido.
#
# Sao dois lugares, e mexer em so um nao resolve:
#   - %AppData%\discord\settings.json -> OPEN_ON_STARTUP (a fonte da verdade)
#   - HKCU\...\Run -> a entrada que o Discord cria a partir daquela configuracao
#
# Mexer so no registro nao adianta: o Discord recria a entrada a partir do
# settings.json. Mexer so no settings.json deixa a entrada atual valendo ate o
# Discord abrir de novo.

[CmdletBinding()]
param(
    # O Discord reescreve o settings.json ao fechar, a partir do que tem em
    # memoria - com ele aberto, a alteracao seria desfeita.
    [switch] $FecharDiscord
)

$ErrorActionPreference = "Stop"

function Set-OpenOnStartupFalse {
    param([string] $Caminho)

    if (-not (Test-Path $Caminho)) { return $false }

    $original = [System.IO.File]::ReadAllText($Caminho)
    Copy-Item $Caminho "$Caminho.bak-vpnlauncher" -Force

    if ($original -match '"OPEN_ON_STARTUP"\s*:\s*(true|false)') {
        $novo = [System.Text.RegularExpressions.Regex]::Replace(
            $original, '("OPEN_ON_STARTUP"\s*:\s*)true', '${1}false')
    }
    elseif ($original -match '^\s*\{\s*\}\s*$') {
        $novo = "{`r`n  `"OPEN_ON_STARTUP`": false`r`n}"
    }
    else {
        # Insere logo apos a PRIMEIRA chave, preservando o resto do arquivo byte a byte.
        #
        # Feito com IndexOf de proposito: o 4o parametro do Regex::Replace estatico e
        # RegexOptions, nao "quantas substituir" - passar 1 ali significa IgnoreCase e
        # injeta a chave em todo '{' do arquivo, inclusive dentro de objetos aninhados
        # como WINDOW_BOUNDS. Aqui nao ha ambiguidade.
        $posicao = $original.IndexOf('{')
        if ($posicao -lt 0) { return $false }

        $novo = $original.Substring(0, $posicao + 1) +
                "`r`n  `"OPEN_ON_STARTUP`": false," +
                $original.Substring($posicao + 1)
    }

    if ($novo -eq $original) { return $true }

    # SEM BOM: o Discord le este arquivo com JSON.parse, que engasga com o BOM
    # que o Set-Content/Out-File do PowerShell 5.1 escreveria.
    $utf8SemBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Caminho, $novo, $utf8SemBom)

    return $true
}

if ($FecharDiscord) {
    foreach ($nome in @("Discord", "DiscordPTB", "DiscordCanary")) {
        Get-Process -Name $nome -ErrorAction SilentlyContinue |
            Where-Object { $_.Id -ne $PID } |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 800   # o Discord grava o settings.json ao sair
}

$ajustados = 0
foreach ($pasta in @("discord", "discordptb", "discordcanary")) {
    $caminho = Join-Path $env:APPDATA "$pasta\settings.json"
    if (Set-OpenOnStartupFalse -Caminho $caminho) {
        Write-Host "settings.json ajustado: $caminho"
        $ajustados++
    }
}

$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
foreach ($valor in @("Discord", "DiscordPTB", "DiscordCanary")) {
    if (Get-ItemProperty -Path $run -Name $valor -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $run -Name $valor -Force
        Write-Host "entrada de inicializacao removida: $valor"
    }
}

if ($ajustados -eq 0) {
    Write-Host "Nenhum settings.json encontrado - confira manualmente em"
    Write-Host "Discord > Configuracoes > Configuracoes do Windows > 'Abrir o Discord'."
    exit 2
}

exit 0
