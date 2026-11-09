# Envia ao GitHub mudanças só de texto e imagens (README, changelogs, capturas), sem gerar versão nova:
# confere segredos, faz push do main e reescreve as notas da Release da versão atual a partir do CHANGELOG.en.md.
# Roda fora do pacote do Claude (enviar-textos.cmd via explorer.exe). Registro: enviar-textos.log, nesta pasta.
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
. (Join-Path $PSScriptRoot 'comum.ps1')
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$repo = 'smendesjunior/WinStatusBar'
$log = Join-Path $PSScriptRoot 'enviar-textos.log'
$lines = New-Object Collections.Generic.List[string]
function Say([string]$text) { Write-Host $text; $lines.Add("$(Get-Date -Format s) $text") }
$notesFile = Join-Path ([IO.Path]::GetTempPath()) ("WinStatusBar-notes-" + [guid]::NewGuid().ToString('N') + '.md')
Push-Location $root
try {
    $gh = Find-Gh
    if (-not $gh) { throw 'GitHub CLI não encontrado. Rode tools\github\configurar-github.cmd.' }
    $pending = Invoke-Native git @('status', '--porcelain') 'git status'
    if ($pending) { throw "Há mudanças sem commit; faça o commit antes de enviar:`n$($pending -join "`n")" }
    Invoke-Native git @('fetch', 'origin', 'main') 'git fetch' | Out-Null
    $changed = @(Invoke-Native git @('diff', '--name-only', '--diff-filter=ACMR', 'origin/main..HEAD') 'git diff')
    if ($changed) { & (Join-Path $PSScriptRoot 'verificar-segredos.ps1') -Range 'origin/main..HEAD' }
    Invoke-Native git @('push', 'origin', 'main') 'git push (main)' | Out-Null
    Say "main enviado ($($changed.Count) arquivo(s) alterado(s))."
    # Descrição, página e tópicos do repositório (inglês), sempre iguais aos daqui.
    $description = "AI command bar for Windows 11: chat with Claude, Codex, Gemini, Copilot, Qira or local models using the active window as context, run verified PC automations straight from the bar, bring every AI into Codex's model picker with one-click model sync, and track AI quotas and hardware live. Per-user install, silent auto-updates."
    $topics = 'windows,windows-11,status-bar,appbar,winui3,dotnet,system-monitor,ai-assistant,claude,codex,gemini,copilot,ollama,automation,desktop-automation,llm,productivity'
    Invoke-Native $gh @('repo', 'edit', $repo, '--description', $description, '--homepage', "https://github.com/$repo/releases/latest", '--add-topic', $topics) 'Descrição do repositório' | Out-Null
    Say 'Descrição, página e tópicos do repositório atualizados.'
    $version = (Select-Xml -Path (Join-Path $root 'Directory.Build.props') -XPath '//Version').Node.InnerText
    $tag = "v$version"
    if (Invoke-Native git @('ls-remote', '--tags', 'origin', "refs/tags/$tag") 'git ls-remote') {
        [IO.File]::WriteAllText($notesFile, (Get-ReleaseNotes $root $version), (New-Object Text.UTF8Encoding $false))
        Invoke-Native $gh @('release', 'edit', $tag, '--repo', $repo, '--notes-file', $notesFile) 'Atualização das notas da Release' | Out-Null
        Say "Notas da Release $tag atualizadas."
    } else { Say "A versão $version ainda não tem Release; notas não alteradas." }
    Say 'CONCLUÍDO'
} catch {
    Say "ERRO: $($_.Exception.Message)"
} finally {
    Pop-Location
    Remove-Item -LiteralPath $notesFile -Force -ErrorAction SilentlyContinue
}
$lines | Set-Content $log -Encoding UTF8
