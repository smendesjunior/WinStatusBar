param(
  [ValidateSet('claude','codex','gemini')][string]$Provider,
  [ValidateSet('login','logout','status','manage')][string]$Action
)
$ErrorActionPreference = 'Stop'
try {
  $cli = $null
  if ($Provider -eq 'claude') {
    $cli = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Claude-Data') -Filter claude.exe -Recurse -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
  } elseif ($Provider -eq 'codex') {
    $cli = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'OpenAI/Codex/bin') -Filter codex.exe -Recurse -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
  }
  if (-not $cli) {
    $candidateDirs = @((Join-Path $env:USERPROFILE '.local/bin'), (Join-Path $env:APPDATA 'npm'), (Join-Path $env:LOCALAPPDATA 'Microsoft/WinGet/Links'))
    $candidateDirs += $env:PATH -split ';'
    foreach ($dir in $candidateDirs) {
      foreach ($ext in @('.exe','.cmd')) {
        $candidate = Join-Path $dir ($Provider + $ext)
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $cli = $candidate; break }
      }
      if ($cli) { break }
    }
  }
  if (-not $cli) { throw 'CLI nao encontrado. Instale pelo site oficial e atualize o estado nas configuracoes.' }
  if ($Provider -eq 'gemini') {
    Write-Host 'No Gemini CLI, use /auth para gerenciar a autenticacao. A chave da barra e removida separadamente nas configuracoes.'
    & $cli
  } elseif ($Provider -eq 'claude') {
    & $cli auth $Action
  } elseif ($Action -eq 'status') {
    & $cli login status
  } else {
    & $cli $Action
  }
  if ($LASTEXITCODE -ne 0) { throw ('O CLI terminou com codigo ' + $LASTEXITCODE + '. Verifique a mensagem acima; nenhuma conclusao de sucesso foi registrada.') }
  Write-Host 'Operacao encerrada pelo CLI. Volte a barra e clique em Atualizar estado local.'
} catch { Write-Host ('ERRO: ' + $_.Exception.Message) -ForegroundColor Red }
[void](Read-Host 'Pressione Enter para fechar')
