# Liga e desliga o inicio automatico do Discord.
#
# Chamado pelo instalador na instalacao (desligar) e na desinstalacao
# (perguntar se religa), mas roda sozinho tambem:
#
#   powershell -ExecutionPolicy Bypass -File desativar-autostart.ps1 -FecharDiscord
#   powershell -ExecutionPolicy Bypass -File desativar-autostart.ps1 -Reativar
#
# Por que desligar: se o Discord subir junto com o Windows, ele registra o IP
# real ANTES do launcher rodar, e a ferramenta inteira perde o sentido.
#
# Por que religar na desinstalacao: a alteracao e na configuracao de OUTRO
# programa. Deixar o Discord sem inicio automatico depois que este launcher foi
# removido e deixar rastro em software de terceiros - o usuario ficaria com um
# comportamento que nunca pediu e sem nada na maquina que explique a mudanca.
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
    [switch] $FecharDiscord,

    # Religa o inicio automatico (usado na desinstalacao).
    [switch] $Reativar,

    # Apaga os .bak que este script deixou. A desinstalacao passa sempre, mesmo
    # quando o usuario decide nao religar: o backup e lixo nosso na pasta do
    # Discord.
    [switch] $LimparBackup
)

$ErrorActionPreference = "Stop"

$Pastas = @("discord", "discordptb", "discordcanary")
$ChaveRun = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

function Set-OpenOnStartup {
    param([string] $Caminho, [bool] $Ativo)

    if (-not (Test-Path $Caminho)) { return $false }

    $valor = if ($Ativo) { "true" } else { "false" }
    $oposto = if ($Ativo) { "false" } else { "true" }

    $original = [System.IO.File]::ReadAllText($Caminho)
    Copy-Item $Caminho "$Caminho.bak-vpnlauncher" -Force

    if ($original -match '"OPEN_ON_STARTUP"\s*:\s*(true|false)') {
        $novo = [System.Text.RegularExpressions.Regex]::Replace(
            $original, "(`"OPEN_ON_STARTUP`"\s*:\s*)$oposto", "`${1}$valor")
    }
    elseif ($original -match '^\s*\{\s*\}\s*$') {
        $novo = "{`r`n  `"OPEN_ON_STARTUP`": $valor`r`n}"
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
                "`r`n  `"OPEN_ON_STARTUP`": $valor," +
                $original.Substring($posicao + 1)
    }

    if ($novo -eq $original) { return $true }

    # SEM BOM: o Discord le este arquivo com JSON.parse, que engasga com o BOM
    # que o Set-Content/Out-File do PowerShell 5.1 escreveria.
    $utf8SemBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Caminho, $novo, $utf8SemBom)

    return $true
}

<#
.SYNOPSIS
Localiza o Update.exe do Discord, para recriar a entrada de inicializacao.

.DESCRIPTION
Mesma fonte que o launcher usa: o proprio Discord anota onde esta instalado, o
que cobre instalacao em outro disco ou pasta personalizada.
#>
function Get-DiscordUpdate {
    foreach ($nome in @("Discord", "DiscordPTB", "DiscordCanary")) {
        $chave = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$nome"
        $local = (Get-ItemProperty $chave -ErrorAction SilentlyContinue).InstallLocation

        if ($local) {
            $update = Join-Path $local "Update.exe"
            if (Test-Path $update) { return @{ Exe = $update; Nome = $nome } }
        }
    }

    foreach ($nome in @("Discord", "DiscordPTB", "DiscordCanary")) {
        $update = Join-Path $env:LOCALAPPDATA "$nome\Update.exe"
        if (Test-Path $update) { return @{ Exe = $update; Nome = $nome } }
    }

    return $null
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
foreach ($pasta in $Pastas) {
    $caminho = Join-Path $env:APPDATA "$pasta\settings.json"
    if (Set-OpenOnStartup -Caminho $caminho -Ativo:$Reativar) {
        $estado = if ($Reativar) { "true" } else { "false" }
        Write-Host "settings.json ajustado (OPEN_ON_STARTUP=$estado): $caminho"
        $ajustados++
    }
}

if ($Reativar) {
    # Recria a entrada de inicializacao com os mesmos argumentos que o Discord
    # usa. Sem isto, o inicio automatico so voltaria a valer depois de o usuario
    # abrir o Discord uma vez, o que nao e obvio para ninguem.
    $discord = Get-DiscordUpdate
    if ($discord) {
        $comando = '"{0}" --processStart {1}.exe --process-start-args "--start-inactive"' -f
                   $discord.Exe, $discord.Nome
        Set-ItemProperty -Path $ChaveRun -Name $discord.Nome -Value $comando
        Write-Host "inicializacao automatica restaurada: $($discord.Nome)"
    }
    else {
        Write-Host "Discord nao localizado; a configuracao foi restaurada, mas a entrada"
        Write-Host "de inicializacao so voltara quando o Discord for aberto uma vez."
    }
}
else {
    foreach ($valor in @("Discord", "DiscordPTB", "DiscordCanary")) {
        if (Get-ItemProperty -Path $ChaveRun -Name $valor -ErrorAction SilentlyContinue) {
            Remove-ItemProperty -Path $ChaveRun -Name $valor -Force
            Write-Host "entrada de inicializacao removida: $valor"
        }
    }
}

if ($LimparBackup) {
    foreach ($pasta in $Pastas) {
        $backup = Join-Path $env:APPDATA "$pasta\settings.json.bak-vpnlauncher"
        if (Test-Path $backup) {
            Remove-Item $backup -Force
            Write-Host "backup removido: $backup"
        }
    }
}

if ($ajustados -eq 0) {
    Write-Host "Nenhum settings.json encontrado - confira manualmente em"
    Write-Host "Discord > Configuracoes > Configuracoes do Windows > 'Abrir o Discord'."
    exit 2
}

exit 0
