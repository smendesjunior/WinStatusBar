param([Parameter(Mandatory=$true)][string]$Job)
$ErrorActionPreference = 'Stop'
$script:sequence = 0
$script:window = $null
$script:sent = $false
$mutex = New-Object Threading.Mutex($false, 'Local\WinStatusBar.CopilotBridge')
$ownsMutex = $false
function Snapshot([string]$text, [bool]$generating, [string]$errorText) {
  $script:sequence++
  $json = @{ Sequence=$script:sequence; Text=$text; Generating=$generating; Error=$errorText } | ConvertTo-Json -Compress
  $temp = Join-Path $Job 'snapshot.tmp'
  [IO.File]::WriteAllText($temp,$json,(New-Object Text.UTF8Encoding($false)))
  Move-Item -LiteralPath $temp -Destination (Join-Path $Job 'snapshot.json') -Force
}
function CheckCancel {
  if (Test-Path -LiteralPath (Join-Path $Job 'stop')) { throw 'Interrompido' }
}
try {
  try { $ownsMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
  if (-not $ownsMutex) { throw 'Outra ponte Copilot esta em execucao. Aguarde e tente novamente.' }
  Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Windows.Forms
  Add-Type @'
using System; using System.Runtime.InteropServices;
public static class CopilotWindow {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int c);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a,uint b,bool c);
 public static void Activate(IntPtr h) { uint p; uint f=GetWindowThreadProcessId(GetForegroundWindow(),out p), t=GetCurrentThreadId(); bool attached=f!=0 && f!=t && AttachThreadInput(f,t,true); try {ShowWindow(h,9);SetForegroundWindow(h);} finally {if(attached) AttachThreadInput(f,t,false);} }
}
'@
  $req = Get-Content -LiteralPath (Join-Path $Job 'request.json') -Raw -Encoding UTF8 | ConvertFrom-Json
  if ([string]::IsNullOrWhiteSpace($req.Text)) { throw 'Pedido vazio.' }
  $expected = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Copilot\Application\copilotapp.exe'
  if ($req.Executable -ne $expected -or -not (Test-Path -LiteralPath $expected)) { throw 'Executavel Microsoft Copilot nao encontrado.' }
  function FindId($root,[string]$id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty),$id
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
  }
  function Nodes($root) { return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) }
  function InvokeExact($root,[string[]]$names) {
    $matches = @(Nodes $root | Where-Object { $_.Current.ControlType -in @([System.Windows.Automation.ControlType]::Button,[System.Windows.Automation.ControlType]::Hyperlink) -and $_.Current.Name -in $names -and $_.Current.IsEnabled -and -not $_.Current.IsOffscreen })
    if ('Novo chat' -in $names) { $matches = @($matches | Where-Object { $_.Current.AutomationId -eq '' -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Hyperlink }) }
    if ($matches.Count -ne 1) { throw 'Controle Copilot ausente ou ambiguo; nenhuma acao executada.' }
    $matches[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  }
  # Match the executable identity as well as the window; never automate a browser tab called Copilot.
  $processes = @(Get-Process copilotapp -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $expected })
  if (-not ($processes | Where-Object MainWindowHandle -ne 0)) { Start-Process -FilePath $expected }
  $watch = [Diagnostics.Stopwatch]::StartNew()
  $editor = $null
  while ($watch.Elapsed.TotalSeconds -lt 35) {
    CheckCancel
    $pids = @(Get-Process copilotapp -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $expected } | ForEach-Object Id)
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)
    foreach ($candidate in $windows) {
      if ($candidate.Current.ProcessId -notin $pids) { continue }
      $candidateEditor = FindId $candidate 'm365-chat-editor-target-element'
      if ($candidateEditor) { $script:window = $candidate; $editor = $candidateEditor; break }
      $login = @(Nodes $candidate | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Current.Name -in @('Entrar','Sign in','Aceitar','Accept') })
      if ($login.Count -gt 0) { throw 'O Copilot exige login ou confirmacao. Conclua manualmente no aplicativo; a barra nao preenche credenciais nem aceita termos.' }
    }
    if ($editor) { break }
    Start-Sleep -Milliseconds 500
  }
  if (-not $editor) { throw 'Janela/editor Copilot indisponivel. Abra o aplicativo e conclua o login manualmente.' }
  $value = $editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  if (-not [string]::IsNullOrWhiteSpace($value.Current.Value)) { throw 'Existe um rascunho no Copilot. Envie ou salve-o antes de usar a ponte.' }
  $existingStop = @(Nodes $window | Where-Object { $_.Current.Name -in @('Parar de gerar','Stop generating') -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button })
  if ($existingStop.Count -gt 0) { throw 'O Copilot ja esta gerando outra resposta. Aguarde antes de usar a ponte.' }
  InvokeExact $window @('Novo chat','New chat')
  Start-Sleep -Milliseconds 900
  $editor = FindId $window 'm365-chat-editor-target-element'
  if (-not $editor) { throw 'O editor nao apareceu apos iniciar novo chat.' }
  $existingReplies = @(Nodes $window | Where-Object { $_.Current.AutomationId -like 'copilot-message-*-title' })
  if ($existingReplies.Count -gt 0) { throw 'Nao foi possivel confirmar um novo chat vazio.' }
  if ($req.Ephemeral) {
    $temporary = @(Nodes $window | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Current.Name -in @('Chat temporário','Temporary chat') })
    if ($temporary.Count -ne 1) { throw 'Modo temporario do Copilot nao encontrado; roteador auxiliar nao executado neste aplicativo.' }
    $toggle = $temporary[0].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $toggle.Toggle(); Start-Sleep -Milliseconds 200 }
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { throw 'Nao foi possivel confirmar chat temporario do Copilot.' }
  }
  $value = $editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  if (-not [string]::IsNullOrWhiteSpace($value.Current.Value)) { throw 'Novo chat contem rascunho; envio cancelado.' }
  CheckCancel
  # Nunca traz a janela do Copilot para frente nem move o foco (07/11/2026).
  $value.SetValue([string]$req.Text)
  Start-Sleep -Milliseconds 200
  if ($value.Current.Value -ne $req.Text) {
    if (-not [string]::IsNullOrWhiteSpace($value.Current.Value)) { throw 'Preenchimento parcial do Copilot. Confira o rascunho; nao foi enviado.' }
    # Teclado simulado removido (07/11/2026): digitava na janela em primeiro plano. Sem preenchimento seguro, recusa.
    throw 'O app Copilot nao aceitou o texto sem teclado simulado; nada foi enviado.'
  }
  CheckCancel
  if (($value.Current.Value -replace '\r\n',"`n").Trim() -ne (([string]$req.Text) -replace '\r\n',"`n").Trim()) { throw 'O texto no Copilot difere do pedido. Confira o rascunho; nada enviado.' }
  $content = FindId $window 'm365-copilot-app-layout-content'
  if (-not $content) { throw 'Area de conversa do Copilot nao encontrada.' }
  InvokeExact $content @('Enviar','Send')
  $script:sent = $true
  $watch.Restart()
  while ($watch.Elapsed.TotalSeconds -lt 120) {
    CheckCancel
    $content = FindId $window 'm365-copilot-app-layout-content'
    if (-not $content) { throw 'A janela do Copilot mudou durante a resposta.' }
    $nodes = @(Nodes $content)
    $generating = @($nodes | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Current.Name -in @('Parar de gerar','Stop generating') }).Count -gt 0
    $replies = @($nodes | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Group -and $_.Current.Name -match '^Copilot said:' })
    $text = ''
    if ($replies.Count -gt 0) { $text = ($replies[-1].Current.Name -replace '^Copilot said:\s*','').Trim() }
    Snapshot $text $generating $null
    Start-Sleep -Milliseconds 400
  }
  throw 'Tempo limite do Copilot; conclusao nao confirmada.'
} catch {
  try { Snapshot '' $false $_.Exception.Message } catch { }
} finally {
  # Only stop a generation started by this job. Never interact with authentication dialogs.
  if ($script:sent -and $script:window) {
    try {
      $content = FindId $script:window 'm365-copilot-app-layout-content'
      $stop = @(Nodes $content | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Current.Name -in @('Parar de gerar','Stop generating') })
      if ($stop.Count -eq 1) { $stop[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    } catch { }
  }
  if ($ownsMutex) { $mutex.ReleaseMutex() }
  $mutex.Dispose()
  [IO.File]::WriteAllText((Join-Path $Job 'finished'),'1')
}
