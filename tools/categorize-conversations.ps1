# Organiza o histórico de conversas da barra em uma pasta por serviço (mesma regra do app). Seguro repetir.
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'diag\categorize-conversations.txt'
$settings = Join-Path $env:LOCALAPPDATA 'WinStatusBar\settings.json'
$custom = ''
if (Test-Path $settings) { try { $custom = (Get-Content $settings -Raw | ConvertFrom-Json).CommandHistoryFolder } catch { } }
$od = (Get-ItemProperty 'HKCU:\Software\Microsoft\OneDrive\Accounts\Personal' -ErrorAction SilentlyContinue).UserFolder
$root = if ($custom) { $custom } elseif ($od) { Join-Path $od 'WinStatusBar\Conversas' } else { Join-Path $env:LOCALAPPDATA 'WinStatusBar\conversas' }
$map = @{ 'claude'='Claude Code'; 'codex'='Codex'; 'gemini'='Gemini'; 'copilot'='Microsoft Copilot'; 'local'='IA local'; 'api'='API'; ''='Outros' }
$moved = 0; $skipped = 0
if (Test-Path $root) {
  foreach ($f in Get-ChildItem -LiteralPath $root -Filter '*.json' -File) {
    try {
      $t = ((Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).Target + '').ToLowerInvariant()
      $name = if ($map.ContainsKey($t)) { $map[$t] } else { $t }
      $dir = Join-Path $root $name
      New-Item -ItemType Directory -Force $dir | Out-Null
      $dest = Join-Path $dir $f.Name
      if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $f.FullName } else { Move-Item -LiteralPath $f.FullName -Destination $dest }
      $moved++
    } catch { $skipped++ }
  }
}
& { "Pasta: $root"; "Movidas: $moved"; "Ignoradas (ilegíveis): $skipped"; Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | ForEach-Object { "{0}: {1}" -f $_.Name, (Get-ChildItem -LiteralPath $_.FullName -Filter '*.json' -File).Count }; "Na raiz: " + (Get-ChildItem -LiteralPath $root -Filter '*.json' -File -ErrorAction SilentlyContinue).Count } | Out-File $out -Encoding utf8
