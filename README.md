# WinStatusBar

Barra nativa do Windows 11 ancorada no topo da tela (AppBar do Shell), em C#/.NET 10 + WinUI 3 (Windows App SDK 1.8).
Reúne monitoramento de CPU, RAM, GPU e rede, cotas do Codex e do Claude Code, a tarefa atual dos agentes de IA
e atalhos do Windows, sem depender de TaskbarQuota, TrafficMonitor, QuotaScope ou outro monitor.

![WinStatusBar](docs/winstatusbar-dark.png)

## Locais

| Item | Caminho |
|---|---|
| Código-fonte / solução | `WinStatusBar.sln` (esta pasta) |
| **Instalador (para qualquer PC)** | `Instalador\WinStatusBar-Setup.exe` |
| Pacote para a Microsoft Store | `Instalador\Store\WinStatusBar_1.0.0.0_x64.msix` (`tools\build-msix.ps1`) |
| Instalado em | `%LOCALAPPDATA%\Programs\WinStatusBar\WinStatusBar.exe` |
| Configurações, cache, log | `%LOCALAPPDATA%\WinStatusBar\` (`settings.json`, `cache\`, `winstatusbar.log`) |
| Desinstalar | Configurações › Aplicativos › WinStatusBar (ou `Uninstall.exe /uninstall`) |

Build completo (testes → publish → instalador): `powershell -File tools\build-release.ps1`.
O resultado vai para `Instalador\` e os intermediários (bin, obj, artifacts) são apagados no fim; use `-KeepIntermediates` para mantê-los.

## Instalar em outro PC

1. Copie `Instalador\WinStatusBar-Setup.exe` para o outro computador (Windows 10 19041+ ou Windows 11, x64).
2. Execute. Não precisa de administrador nem de .NET instalado (runtime e Windows App SDK vão embutidos).
   Como o instalador não é assinado digitalmente, o SmartScreen pode avisar: "Mais informações" › "Executar assim mesmo".
3. A barra se adapta sozinha: mostra só as IAs instaladas (Codex, Claude Code, IA local), temperatura só se houver sensor
   (o sensor Lenovo exige a autorização em Configurações › Hardware), GPU integrada com memória compartilhada,
   e integra o Claude Code se ele existir (ou quando for instalado depois).
4. Desinstalar: Configurações do Windows › Aplicativos › WinStatusBar.

Para gerar um novo instalador é preciso o .NET SDK 10 (`winget install Microsoft.DotNet.SDK.10`).

## Estrutura

```
src/WinStatusBar.Core       coletores e lógica (sem UI): Metrics/, Ai/Codex, Ai/Claude, Ipc/, Settings/, Integration/
src/WinStatusBar            app WinUI 3: Views/ (BarWindow, SettingsWindow), ViewModels/, Services/ (AppBar, bandeja, ícones, hub), Interop/
src/WinStatusBar.Collector  coletor mínimo chamado pelos hooks/statusLine do Claude Code → Named Pipe
src/WinStatusBar.Setup      instalador/desinstalador por usuário (payload embutido, sem administrador)
tests/WinStatusBar.Tests    testes xUnit (cálculos, JSON do Codex/Claude, mescla de settings, prioridade)
src/WinStatusBar/Package    manifesto MSIX e logotipos da Store (usados só no build empacotado)
tools/                      build-release (Setup.exe), build-msix (Store), make-icon, make-store-assets, stop-bar
```

Interfaces substituíveis: `ISystemMetricsProvider<T>`, `IAiQuotaProvider`, `IAgentActivityProvider`.
Toda a coleta roda num único laço assíncrono fora da thread de UI (`MonitoringHub`); agentes e cotas chegam por eventos.

## Barra (AppBar)

- `SHAppBarMessage` com `ABM_NEW / ABM_QUERYPOS / ABM_SETPOS / ABM_REMOVE`, callback `ABN_POSCHANGED` e `ABN_FULLSCREENAPP`.
- 100 % da largura do monitor, 48 epx de altura (72 px a 150 %), janelas maximizadas começam abaixo dela.
- **Sem sombra, sem borda e sem cantos arredondados (temas claro e escuro):** `WS_POPUP` puro, filtro de
  `WM_STYLECHANGING` (o WinUI tentava reaplicar `WS_DLGFRAME`), `WM_NCCALCSIZE` sem área não-cliente,
  `DWMWA_NCRENDERING_POLICY = disabled`, `DWMWCP_DONOTROUND`, `DWMWA_BORDER_COLOR = none`. Verificado pixel a pixel.
- Fora do Alt+Tab e da taskbar (`WS_EX_TOOLWINDOW`, `IsShownInSwitchers = false`), sempre topmost (exceto atrás de app em tela cheia, configurável).
- Re-registro automático ao reiniciar o Explorer (`TaskbarCreated`), resolução/DPI (`WM_DISPLAYCHANGE`, `WM_DPICHANGED`),
  retomada da suspensão, troca de monitor principal; um ou todos os monitores.
- Encerramento (inclusive pelo desinstalador, via evento nomeado) faz `ABM_REMOVE` e libera a área imediatamente.
- Mica Alt (padrão), Mica, Acrylic ou sólido; o estado "ativo" do material é forçado para não cair na cor de fallback.

## Fontes de dados e decisões técnicas

| Módulo | Fonte |
|---|---|
| CPU % | PDH `\Processor Information(_Total)\% Processor Utility` (a métrica do Gerenciador de Tarefas); fallback `GetSystemTimes` |
| CPU GHz | `Processor Frequency × % Processor Performance / 100` (frequência efetiva, não a nominal) |
| RAM | `GlobalMemoryStatusEx`: usada = `ullTotalPhys − ullAvailPhys` |
| GPU | DXGI (`IDXGIFactory1/IDXGIAdapter1::GetDesc1`, LUID) + PDH `GPU Adapter Memory(luid_*)\Dedicated/Shared Usage` (uso global, todos os processos) e `GPU Engine(*)\Utilization Percentage` (maior motor). **Não** usa `QueryVideoMemoryInfo.CurrentUsage` (é por processo). Total dedicado: `HardwareInformation.qwMemorySize` do driver (igual ao Gerenciador: 6,0 GB) com fallback para `DedicatedVideoMemory` do DXGI (5,8 GB, desconta reservas). Prefere a GPU dedicada; selecionável. |
| Rede | `GetIfTable2` (`MIB_IF_ROW2`), soma só interfaces físicas ativas (sem filtros/VPN/virtuais → nada contado duas vezes), tolera reinício de contador; interface manual opcional |
| Cotas Codex | protocolo oficial `codex app-server --listen stdio://` → `initialize` + `account/read` + `account/rateLimits/read` (processo aberto sob demanda a cada 10 min, com backoff) + `token_count.rate_limits` dos rollouts locais em tempo real. Nenhum token é lido. |
| Atividade Codex | leitura incremental e somente-leitura de `~/.codex/sessions/**/rollout-*.jsonl` (FileSystemWatcher) + títulos reais de `~/.codex/session_index.jsonl`. Respeita `CODEX_HOME`. Progresso numérico somente a partir das etapas de `update_plan`. |
| Cotas Claude | Consulta automática à conta (`api.anthropic.com/api/oauth/usage`) ao iniciar e a cada 5 minutos, com o login existente do Claude Code; também aceita `rate_limits` da statusLine. Respeita Retry-After e mantém cache apenas de percentuais e renovação. |
| Atividade Claude | hooks `SessionStart, UserPromptSubmit, PreToolUse, PostToolUse, PostToolUseFailure, PermissionRequest, Notification, SubagentStart/Stop, TaskCreated/Completed, Stop, StopFailure, SessionEnd` (todos `async`). Título real da sessão = última entrada `custom-title` do transcript; progresso real = itens concluídos de TodoWrite/plano. |

**Agente "Local" (modelos no próprio computador, qualquer cliente):** `LocalAiProvider` detecta servidores de inferência local
(Ollama, LM Studio, llama.cpp, Foundry Local, KoboldCpp, LocalAI, Jan, llamafile + nomes extras em Configurações › IA),
incluindo os processos filhos que fazem a inferência (o Ollama atual usa `lib\ollama\llama-server.exe`). "Executando" quando a
árvore do servidor usa GPU (contadores GPU Engine por PID) ou CPU acima do limiar, com histerese de 6 s. Modelo pelo `/api/ps`
do Ollama (localhost). O cliente sai da tabela TCP (pares loopback ESTABLISHED; o socket LISTEN do Ollama nem sempre aparece),
atravessando intermediários locais e subindo a árvore de processos: Word/Excel/PowerPoint → WebView2 → `servidor.py` (:3000) → Ollama
aparece como "Gerando para Word". Codex com modelo local aparece como "Gerando para Codex". Quando há geração local, ela vence
as outras sessões "Executando" (pedidos de autorização/resposta continuam acima); servidor ocioso só aparece no painel de sessões.

![Local](docs/winstatusbar-local.png)

**Integração com o Claude Code** (`~/.claude/settings.json`): mescla apenas `hooks` e `statusLine`, com backup
`settings.json.winstatusbar-backup-*`. A statusLine existente (TrafficMonitor/CotaStatusLine) é preservada — o coletor
a executa em cadeia e repassa a saída byte a byte; o objeto original fica em Base64 no próprio comando e é restaurado
exatamente na desinstalação. Os comandos ficam sem aspas porque, sem Git Bash, o Claude Code usa PowerShell,
onde `"C:/x.exe" arg` é erro de sintaxe.

**Privacidade:** o coletor envia só metadados (evento, ferramenta, nome do arquivo, descrição curta, até 90 caracteres do pedido)
por um Named Pipe com ACL restrita ao usuário. Nada de conversas, conteúdo de arquivos, comandos completos ou credenciais.
Sem telemetria. Modo de privacidade oculta títulos e descrições.

## Resultados dos testes (04/11/2026)

- Local: geração real no Ollama direta e via `servidor.py` do suplemento do Office → "Local · Executando · Gerando para …" → "Concluído".
- 55 testes automatizados aprovados (cálculos, unidades, rede, GPU, JSON do app-server e dos rollouts, statusLine/hooks, máquina de estados, prioridade, mescla/remoção do settings.json).
- Barra imediata, largura total, sem margens/sombra/borda (claro e escuro), 72 px a 150 % = taskbar; maximizada começa em y=72.
- Reinício do Explorer: área continua reservada. Fechar/desinstalar: área volta a Y=0.
- RAM idêntica ao sistema; CPU/GHz pela mesma fonte do Gerenciador; VRAM 0,5 / 6,0 GB igual ao Gerenciador.
- Codex: cotas reais (5h 97 % e 7d 86 % restantes, plano team), título e estado da thread.
- Claude: hooks reais ponta a ponta (sessão nova → "Executando" → "Concluído"); esta sessão aparece com o título real.
- Desinstalação: processo, pasta, dados, registro, atalho e Run removidos; `settings.json` do Claude **idêntico** ao original.
- Consumo: ~0,9 % de CPU no pior caso (animação indeterminada contínua), ~100 MB de memória privada / ~190 MB working set.

## Limitações conhecidas

- **Cotas do Claude**: a consulta automática usa o login existente do Claude Code e precisa de permissão `user:profile`. Se o login expirar, o app solicita que você o renove no Claude Code; não altera nem renova credenciais por conta própria. A API de uso pode mudar ou limitar consultas; erros e a data do último dado ficam visíveis. Não exige terminal aberto.
- Sessões já abertas do Claude Code só passam a enviar hooks depois de reiniciadas (o Claude lê os hooks no início).
- Atividade de threads do app desktop do Codex vem dos rollouts locais (o app desktop usa seu próprio app-server por stdio,
  sem socket compartilhado); espera por aprovação aparece quando o Codex registra o evento no rollout.
- WinUI 3 self-contained ocupa ~260 MB em disco (runtime .NET + Windows App SDK + ReadyToRun); instalador ~139 MB.
- Cada execução do instalador cria um novo backup do `settings.json` do Claude (podem ser apagados manualmente).

## Temperaturas

CPU: interface WMI `root/wmi:LENOVO_GAMEZONE_DATA.GetCPUTemp` já presente no Lenovo.
Requer executar o app como administrador, sem instalar drivers ou programas. Configurações → GPU → Acessar sensor Lenovo.
GPU NVIDIA: `nvidia-smi` do driver existente. Sensores `CPU Proximity` publicados por provedores já existentes têm preferência.
A fonte real aparece ao passar o mouse na temperatura e em Diagnóstico. Nenhuma leitura é renomeada como Proximity.
Inicialização automática mantém as permissões normais; a autorização de administrador vale para a execução atual.
O pacote não inclui nem instala PawnIO, Intel DTT, Libre Hardware Monitor ou outro monitor externo.

## Correção de cotas e lista de rede — 04/11/2026

- A integração dos hooks indica somente que a atividade das sessões está configurada; não confirma recebimento de cotas.
- A consulta usa a credencial do Claude Code somente para autenticar uma requisição HTTPS de metadados ao serviço Anthropic.
  O token não entra no cache, log, IPC ou interface; credenciais não são alteradas e redirecionamentos HTTP são desativados.
- A consulta roda fora do ciclo de hardware, ao abrir e a cada 5 minutos; erros 429 respeitam Retry-After, com mínimo de 5 minutos.
- A lista de rede mostra somente adaptadores físicos Ethernet, Wi-Fi e de banda larga móvel. Filtros WFP/QoS,
  miniportas WAN, loopback e adaptadores virtuais ficam fora; conexões físicas desconectadas continuam selecionáveis.
- As opções indicam o estado da conexão; o nome completo da placa fica no tooltip.

## Menu de agentes, detalhes de cotas e adaptação a outros PCs — 04/11/2026

- **Menu de agentes** (clique no agente ou na tarefa): material Acrylic; uma linha para "Automático" e uma para cada agente
  instalado neste PC (Codex, Claude Code, IA local), com a tarefa atual, estado e sessões. A escolha define o que a barra mostra.
  ChatGPT (chats) e Claude (chat) não aparecem porque não expõem localmente a tarefa em andamento.
- **Detalhes das cotas** (clique nas cotas): cada período com uso, disponível e renovação, barras na cor de destaque do Windows,
  limites extras da conta (Claude: Opus/Sonnet 7d etc., detectados sem lista fixa; Codex: `rateLimitsByLimitId`),
  créditos e **resets avulsos** do Codex (`rateLimitResetCredits`). Correção: as chamadas ao app-server agora são sequenciais
  (antes falhavam com "stream in use" e só os dados do rollout chegavam, sem resets nem plano).
- **Cotas em tempo real** (padrão, configuração 0): Codex por eventos `token_count` + app-server a cada minuto;
  Claude pela conta a cada minuto respeitando `Retry-After`. Outros intervalos: 1–60 min, aplicados imediatamente.
- **Rede**: ícone do tipo de conexão (Wi-Fi E701, cabo E839, banda larga móvel EC05) e setas menores.
- **Outros PCs**: cotas só das IAs instaladas; Codex encontrado também via winget, npm (`codex.cmd`), scoop e PATH;
  temperatura só com sensor real (sem "— °C"); GPU integrada mostra memória compartilhada; integração do Claude Code
  aplicada sozinha se ele for instalado depois (exceto se o usuário a removeu); resumo "Detecção deste PC" no Diagnóstico.

## Microsoft Store (MSIX)

Pacote pronto: `Instalador\Store\WinStatusBar_1.0.0.0_x64.msix` (≈100 MB, x64, full trust, Windows App SDK self-contained).
O mesmo código serve às duas versões; `PackageContext.IsPackaged` decide o comportamento.

| | Instalador tradicional (`Setup.exe`) | Versão Store (MSIX) |
|---|---|---|
| Inicialização com o Windows | chave `HKCU\...\Run` | `StartupTask` do manifesto (Configurações › Aplicativos › Inicialização) |
| Coletor do Claude Code | `%LOCALAPPDATA%\Programs\WinStatusBar\WinStatusBar.Collector.exe` | alias fixo `%LOCALAPPDATA%\Microsoft\WindowsApps\WinStatusBar.Collector.exe` |
| Integração com o Claude Code | automática (backup + mesclagem) | **opt-in**: botão nas configurações › IA |
| Sensor Lenovo (administrador) | tarefa autorizada pelo usuário | não incluído; mostra só leituras sem elevação |
| Remoção | `Setup.exe --uninstall` restaura tudo | pelo Windows; **remova antes a integração do Claude** nas configurações (o MSIX não executa limpeza ao desinstalar, e os hooks ficariam apontando para um alias inexistente) |

Testado em 04/11/2026 neste PC (modo de desenvolvedor, registro do layout): a barra abriu empacotada, o alias do coletor
respondeu à statusLine, a `StartupTask` foi ativada, o `settings.json` do Claude não foi alterado; depois o pacote foi removido
e a instalação tradicional religada.

### Gerar o pacote

```
powershell -File tools\build-msix.ps1 -IdentityName "<Package/Identity/Name>" -Publisher "<CN=...>" -PublisherDisplayName "<nome>"
```

Os três valores estão em Partner Center › seu app › Gerenciamento de produto › Identidade do produto. Sem parâmetros o script
usa a identidade oficial `JuniorMendes.WinStatusBar`, publicador `Junior Mendes`, reservada no Partner Center. A versão vem de
`Directory.Build.props` + `.0` (a Store exige revisão 0). O `.msix` não é assinado: a Store assina na publicação.
Logotipos: `tools\make-store-assets.ps1` → `src\WinStatusBar\Package\Assets`.

### Publicar

1. Criar conta de desenvolvedor no Partner Center e reservar o nome do app.
2. Gerar o pacote com a identidade reservada e enviá-lo em uma nova submissão.
3. Declarar a capacidade restrita `runFullTrust`. Justificativa: AppBar Win32, contadores PDH, tabela TCP e ícone na bandeja.
4. Política de privacidade (obrigatória por usar internet): nada é enviado a terceiros. O token do Claude Code e a conta do
   Codex são lidos localmente apenas para consultar as cotas nos serviços oficiais da Anthropic e da OpenAI.
5. Risco de certificação restante: o uso do login do Claude Code para ler cotas (endpoint não documentado). Se for recusado,
   desligue a consulta da conta na versão Store; a atividade das sessões continua pelos hooks.

## Seletor de IA

O seletor usa contorno de 1 epx, cantos de 6 epx e recursos de cor do WinUI 3.
Durante atividade, o contorno acompanha a cor de destaque do Windows, com preenchimento sutil;
em repouso, usa contorno neutro. O botão mantém hover, pressionamento, foco de teclado e o painel de sessões nativos.

## Arraste e autorização permanente de CPU — 04/11/2026

A ordem dos módulos usa ObservableCollection e reordenação nativa do ListView do WinUI 3.
Cada linha tem alça de arraste à esquerda, sem setas; a ordem e a visibilidade são salvas após soltar.

A autorização permanente cria uma tarefa por usuário no Agendador do Windows, com gatilho de logon e
execução elevada somente do componente WinStatusBar.Sensors. A barra continua sem elevação.
O componente lê exclusivamente GetCPUTemp e entrega temperatura e data por um pipe local de saída;
não recebe comandos nem acessa as credenciais das IAs. As DLLs e o runtime do componente ficam em
Program Files/WinStatusBar/Sensors, com escrita restrita a Administradores e SYSTEM.
O usuário normal pode consultar/iniciar a tarefa, mas não editar a ação elevada.
Não instala PawnIO, serviços, drivers ou softwares de terceiros. A autorização permanece após fechar,
reabrir ou reiniciar o Windows e pode ser revogada nas configurações. A desinstalação remove a tarefa
mediante autorização do Windows. Atualizações da barra preservam a autorização existente.

## Seleção rápida de GPU e menus — 04/11/2026

Clique no módulo GPU da barra para escolher Automático (preferir dGPU), dGPU dedicada ou iGPU integrada.
O menu lista os nomes reais dos adaptadores detectados e marca a opção salva nas configurações.
A seleção é persistida e a leitura é atualizada imediatamente, em todas as barras.
Os menus de GPU, mais opções e bandeja usam MenuFlyout do WinUI 3 com Desktop Acrylic;
os painéis de sessões e cotas também usam Desktop Acrylic. Os seletores das configurações
mantêm os controles WinUI 3 nativos. O Windows pode usar o fundo sólido de acessibilidade
quando efeitos de transparência estão desativados ou o material não está disponível.

## Tipografia da taskbar — 04/11/2026

Textos da barra em Segoe UI Variable, peso regular, com hierarquia: linha principal 12 epx
e entrelinha 16 epx; valores e informações secundárias 11 epx e entrelinha 15 epx.
Nas cotas, a linha de 5h usa 12 epx e a linha de 7d usa 11 epx. Ícones principais e atalhos: 17 epx, para equilibrar o texto em duas linhas.
Altura padrão: 48 epx (72 pixels a 150%), com o DPI aplicado pelo Windows.
A opção de escala adicional continua disponível nas configurações.

## Área de transferência integrada (antigo Windows Clipmanager) — 04/11/2026

O Windows Clipmanager agora roda dentro do WinStatusBar (`src/WinStatusBar/Clip`), num processo só:
Win+V (histórico) e Win+. / Win+; (emojis) abrem o painel flutuante junto ao cursor de texto ou ao mouse;
o botão de atalho na barra abre o mesmo painel logo abaixo dele. O histórico continua em
`%APPDATA%\Windows Clipmanager` (sem migração). A instalação avulsa foi removida do Windows
(processo, inicialização automática e atalho do menu Iniciar; a pasta foi para a Lixeira).

## Configurações dos recursos novos — 04/11/2026

| Página | Opção | Padrão |
|---|---|---|
| Área de transferência | Histórico e emojis · Win+V · Win+./Win+; · Colar ao escolher · Pausar captura | ligado (pausa desligada) |
| Área de transferência › O que guardar | Imagens (máx. 20 MB) · Arquivos e pastas · Cópia de segurança (até 25 MB) · Apps ignorados | KeePass, KeePassXC, 1Password, Bitwarden |
| Área de transferência › Limites | Quantidade máxima · Apagar sem uso há mais de · Espaço máximo em disco · Limpar histórico | sem limite |
| Aparência › Efeitos | Brilho no agente em atividade · Linha de separação do fundo adaptativo | ligados |
| Aparência › Botões de atalho | Área de transferência · Gerenciador de Tarefas · Configurações · Microsoft Store | todos |
| Hardware e rede › Painéis de detalhes | Abrir painel ao clicar · Aplicativos no painel da CPU · Permitir finalizar tarefas · Teste de velocidade | ligados |

Itens fixados nunca são apagados pelos limites. Conteúdo que o app de origem marca como sensível
(ex.: gerenciadores de senha) nunca entra no histórico, independentemente da lista de apps ignorados.

## Cotas do Claude: renovação do login — 04/11/2026

O login em `~/.claude/.credentials.json` vence a cada poucas horas e o app Claude (desktop) não atualiza
esse arquivo. Com autorização do usuário, a barra renova o login pelo mesmo processo do Claude Code
(`grant_type=refresh_token` em `platform.claude.com/v1/oauth/token`, client_id público do Claude Code)
e grava accessToken/refreshToken/expiresAt no mesmo arquivo, de forma atômica e preservando os demais campos.
Tokens não vão para logs, IPC nem para outros arquivos. A janela de cotas do Claude mostra também o
"Uso desta semana por produto" (Claude Code, Conversas, Cowork, Outro), lido de `seven_day_breakdown`.

## Ícones aprovados — 04/11/2026

WinStatusBar usa gráficos suaves em camadas azuis, ciano e lilás com detalhes rosa e verde.
Clipboard usa a prancheta âmbar, folha creme e camada coral.
Os PNGs em `src/WinStatusBar/Assets` são as fontes dos ícones; preservar sua transparência e proporções.
`tools/make-icon.ps1` e `tools/make-clipboard-icon.ps1` geram os ICOs multi-tamanho;
`tools/make-store-assets.ps1` gera as imagens da Store a partir do PNG aprovado do WinStatusBar.

## Cores dos temas — 04/11/2026

Pincéis obtidos em C# para painéis persistentes são resolvidos por `ThemeBrushes` no tema efetivo
do aplicativo, usando recursos nativos WinUI. Evitar obter diretamente `Application.Current.Resources`
para cores dependentes do tema: isso pode fixar a paleta global escura dentro de um painel claro.
Ao mudar o tema, `App.UpdateTheme` atualiza os pincéis compartilhados. XAML continua usando `ThemeResource`.

## Identidade oficial da Store — 04/11/2026

Store ID: 9PCM3X6JJRW6. Package Name: JuniorMendes.WinStatusBar.
Publisher: CN=9FBF1CB5-AD0B-451F-B174-C8D6B7F4A349. PublisherDisplayName: Junior Mendes.
O manifesto declara apenas o aplicativo principal visível. O alias WinStatusBar.Collector.exe
é uma extensão desse aplicativo que aponta para o executável auxiliar incluído no pacote;
não há mais uma aplicação Collector oculta com AppListEntry=none.
A capacidade runFullTrust continua necessária e deve ser justificada na certificação.

## Idiomas — 05/11/2026

Recursos nativos MRT Core / Resources.resw em português brasileiro, inglês, espanhol, japonês,
chinês simplificado e francês. Configurações › Geral › Idioma do aplicativo permite escolher
Automático (preferências do Windows) ou um dos seis idiomas. Reinicie o aplicativo após alterar.
O MSIX declara os seis idiomas e o instalador tradicional inclui o mesmo PRI multilíngue.
Formatação de números e datas acompanha a cultura selecionada. Os nomes de processos, marcas,
comandos e identificadores técnicos são preservados. O catálogo de emojis usa anotações Unicode CLDR
locais em cada idioma, incluindo nomes e palavras-chave de busca.
A tradução ocorre nos recursos incluídos no pacote; não há chamadas a serviços de tradução durante o uso.
As traduções são geradas automaticamente e revisadas em termos principais; revisão linguística por
falantes nativos continua recomendável, sobretudo em japonês e chinês.
