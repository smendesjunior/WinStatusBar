# Registro de mudanças do WinStatusBar

As mudanças relevantes de cada versão entregue. O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e as versões seguem o [versionamento semântico](https://semver.org/lang/pt-BR/):
- **PATCH** para correções;
- **MINOR** para novidades compatíveis;
- **MAJOR** para mudanças incompatíveis.

A compilação (data e hora UTC) é o que o atualizador compara. A versão é para as pessoas.

## [1.10.2] - 2026-11-11 · compilação 20261111193117

### Corrigido
- Codex: a janela de contexto declarada para as IAs da barra passa de 10 milhões para 2 milhões de tokens. Continua acima da de qualquer modelo da OpenAI (a troca de modelo segue sem compactar), mas uma conversa muito longa volta a ser compactada pelo Codex antes de o pedido passar do limite de 64 MB do encaminhador.
- Codex: um pedido grande demais para o encaminhador mostra "Conversa grande demais… Use /compact ou comece uma conversa nova", em vez de "Invalid compressed request".

## [1.10.1] - 2026-11-11 · compilação 20261111191845

### Corrigido
- Codex: trocar para uma IA da barra (Gemini, Copilot, Gemini pela API…) no meio de uma conversa longa, por exemplo depois que a cota do modelo anterior acabou, gerava uma série de erros "Você atingiu seu limite de uso" e "Otimização interrompida", qualquer que fosse o modelo escolhido. As IAs da barra passam a declarar ao Codex uma janela de contexto grande (o encaminhador já decide o que vai para cada IA), e o Codex deixa de compactar a conversa na troca.
- Codex: se o Codex ainda pedir uma compactação a uma IA da barra, o encaminhador responde no formato novo que ele espera, com um resumo da conversa; esse resumo é lido nos pedidos seguintes e vira mensagem comum se a conversa voltar para a OpenAI.

### Alterado
- Seletor de modelos do Codex: os modelos pela chave da API mostram o nome do modelo primeiro e a origem depois ("Gemini 3 Flash Preview · Google API"), para o nome não ser cortado.

## [1.10.0] - 2026-11-11 · compilação 20261111163307

### Adicionado
- Escolha por mim: quando não há nenhuma conta ou API disponível, a pergunta vai para a IA local (se estiver ligada) e, por fim, para o Lenovo Qira, em vez de parar num aviso.
- Escolha por mim: o Lenovo Qira pode ser escolhido quando o pedido cita o Qira ou depende de um recurso dele (com o Qira instalado e ligado).
- Escolha por mim: a escolha leva em conta o que cada IA faz, como ela é cobrada (assinatura, por uso ou sem custo por pedido), a cota usada do Claude e do Codex, os créditos do Qira e se há anexos, modo Editar ou pesquisa na web.

### Alterado
- Escolha por mim: só o começo de um pedido longo (2.000 caracteres) é enviado ao modelo que faz a escolha.
- Escolha por mim: as listas de modelos das contas e APIs são reaproveitadas por 5 minutos, e uma lista lenta não atrasa mais as outras.
- Uma conversa em IA local continua nela com Escolha por mim selecionado.

### Corrigido
- Escolha por mim: modelos Gemini maiores eram tratados como leves, porque "mini" aparece dentro de "gemini". O mesmo erro afetava o modelo usado no roteiro de tarefas.
- Escolha por mim: uma resposta do modelo auxiliar com cercas de código ou texto em volta era descartada e valia o destino padrão. Agora ela é lida e, se não servir, o próximo modelo auxiliar tenta.

## [1.9.0] - 2026-11-11 · compilação 20261111162310

### Adicionado
- IAs da barra em outros programas do PC: o encaminhador local (porta 11435) agora lista as IAs configuradas na barra (`/codexias/modelos`) para programas do próprio computador, como o suplemento "IA Local" do Word, Excel e PowerPoint, que passa a oferecer Gemini, DeepSeek, Grok, Lenovo Qira e as APIs próprias ao lado dos modelos locais. As chaves continuam só no Cofre de Credenciais do Windows.

### Alterado
- O encaminhador também roda com a ligação do Codex desligada quando um programa do PC usa as IAs da barra (marca `office.json`, gravada pelo próprio programa). Desligar a ligação com o Codex não o encerra nesse caso.

## [1.8.0] - 2026-11-11 · compilação 20261111152910

### Adicionado
- Memória das IAs sob demanda: escolher uma IA no chat não abre mais nada. A página oculta do Gemini e do Copilot e o Lenovo Qira só abrem quando você envia a pergunta, fecham pouco depois da resposta e, ao trocar de IA, os da anterior fecham na hora (o modelo da IA local também é liberado na troca).
- Configurações › IAs e contas › Memória: "Manter a IA aberta depois da resposta" (fechar logo, 1 minuto, 5 ou 15 minutos; padrão 1 minuto).
- Painel de rede: o botão do Hotspot móvel fica em destaque quando ligado e mostra quantos dispositivos estão conectados.
- Atalhos de voz (Chat da barra): o campo grava a combinação que você pressionar, em vez de exigir a digitação.
- Conectores do Claude: a lista lida em "Verificar" mostra cada conector com estado colorido (conectado, precisa de login, falhou), nome e origem; "Detectar" acrescenta de uma vez aos sempre permitidos os conectores conectados na conta.

### Alterado
- Lenovo Qira: a barra sobe o núcleo do Qira no primeiro pedido (resposta em cerca de 30 s com tudo fechado, poucos segundos nas seguintes) e encerra o Qira inteiro depois do uso, liberando de 1,8 a 2,5 GB. Só o Qira aberto pela barra é encerrado; com a janela do aplicativo Lenovo aberta, ele fica ligado.
- As IAs no Codex seguem a mesma regra e a mesma espera para as páginas ocultas e para o Qira.

### Corrigido
- Abrir o painel com o Qira escolhido iniciava (e às vezes reiniciava) o Qira inteiro sem nenhuma pergunta.
- As páginas ocultas do Gemini e do Copilot ficavam abertas por 15 minutos depois de cada resposta, e para sempre depois de fechar a janela de login ou de sair da conta.
- Cota do Codex: depois de uma atualização do app do Codex, a barra continuava procurando o programa na pasta da versão antiga e mostrava um erro em inglês até ser reiniciada. Agora ela acha a versão nova sozinha, e o erro ganhou explicação em português.

## [1.7.0] - 2026-11-11 · compilação 20261111140800

### Alterado
- Configurações reorganizadas: 13 páginas viraram 10. Geral, Barra e aparência, IAs e contas, Cotas e tarefas, Chat da barra, Codex e modelos, Hardware, Rede e bateria, Área de transferência e, no rodapé, Sobre e suporte.
- IAs e contas (página nova): o login, as chaves e os conectores de cada IA saíram do meio de "IA e tarefas" e ficam num lugar só, com a API própria ("Outra IA por chave").
- Barra e aparência: os botões de atalho ficam logo abaixo dos itens da barra, e a intensidade da cor fica junto do fundo.
- Chat da barra: todas as permissões num grupo (edição de arquivos, acesso ao PC e conectores); os atalhos de voz ganharam título.
- Cotas e tarefas: o que acompanhar, as cotas na barra e a tarefa na barra em três grupos; a integração do Claude Code e os outros programas de IA local foram para "Avançado".
- Hardware: os painéis de detalhes abrem a página; frequência das leituras e drivers de sensores ficam em "Avançado".
- Sobre e suporte reúne Atualizações, Diagnóstico e Sobre. "O que mudou" fica recolhido e só é montado ao abrir.
- Nomes mais claros: "Avisos do WinStatusBar", "Mostrar as cotas como", "Atualizar cotas agora", "Tipo de API", "Endereço do serviço (URL)", "Remover chave", "Instalar driver de um arquivo…", entre outros.
- Os atalhos da barra abrem a página certa: "Gemini · configurar…" e "Configurar" do menu de agentes levam a IAs e contas; "Configurações da IA…" leva a Chat da barra.
- Títulos das páginas, dos grupos e das opções novas traduzidos nos seis idiomas.

### Removido
- Opções repetidas: o interruptor da bateria (fica em "Itens da barra"), o segundo botão da pasta de dados, o segundo botão de login do Gemini e o "Entrar/Sair" do DeepSeek e do Grok, que fazia o mesmo que "Configurar chave…" e "Remover chave". Nenhuma preferência salva mudou.

### Corrigido
- Atualização: se a troca de arquivos falhasse, a barra fechava e reabria a cada minuto. Agora são 3 tentativas por compilação e depois uma espera de 6 horas; a barra volta a abrir mesmo se um passo final falhar, e a atualização espera uma resposta de IA em andamento.
- Preferências: um settings.json ilegível não zera mais tudo em silêncio (tenta a cópia de segurança e a do OneDrive antes do padrão).
- Claude Code: em perfis com espaço no caminho, a barra duplicava os próprios ganchos no settings.json do Claude a cada 10 minutos.
- Chat: Enter duas vezes não duplica a pergunta; um erro do Claude não é mais salvo como resposta; respostas por API não são cortadas em serviços como Azure e OpenRouter; a rolagem não puxa para o fim quando você rolou para cima.
- Barra: Alt+F4 não deixa mais a barra na tela com os painéis mortos; janelas antigas são liberadas da memória a cada troca de monitor.
- Executor: excluir ou sobrescrever em pasta de rede ou unidade removível é recusado (não há Lixeira ali); "encerrar processo" recusa as partes do Qira e a própria barra.
- IAs no Codex: o encaminhador é conferido a cada minuto e religado se cair; a continuação pela API tenta de novo depois de uma falha passageira de rede.
- "Atualizar cotas agora" mostra que está consultando; o intervalo e a forma de mostrar as cotas ficam disponíveis mesmo com o Codex e o Claude desligados.
- Chaves de API mascaradas no registro (log) da barra.

## [1.6.1] - 2026-11-11 · compilação 20261111033512

### Corrigido
- Menu da cota do Codex: o aviso de bloqueio da conta foi para o rodapé, junto das conversas paradas, num cartão separado das cotas, e ficou com duas linhas (motivo; quando volta e de onde veio a informação).
- Menu da cota: a barra de rolagem aparecia mesmo com espaço sobrando na tela (o limite de altura padrão do menu cortava em cerca de 760 pontos); agora o menu cresce até o fim da tela e só rola se não couber.

## [1.6.0] - 2026-11-11 · compilação 20261111031856

### Adicionado
- IAs da barra no Codex: Gemini e Copilot pela conta, APIs com chave, modelos locais e o Claude sem a opção de comandos agora trabalham nos arquivos. Eles pedem cada comando ou arquivo a gravar, e o próprio Codex executa, com as permissões da conversa e mostrando cada passo.
- Trocar de IA no meio de uma conversa do Codex: quem assume recebe o pedido original, os comandos e resultados de antes (de qualquer modelo), o aviso de que a resposta anterior foi interrompida e as regras do projeto (AGENTS.md).
- Configurações › Modelos: opção para ligar ou desligar o Continuar pela API.

### Alterado
- Menu da cota do Codex: as conversas paradas pela cota e o Continuar pela API foram para o rodapé, na largura das duas colunas (resumo e botão à esquerda, andamento à direita).

### Corrigido
- Compactar uma conversa do Codex com uma IA da barra não apaga mais o trabalho feito: fica um resumo com as falas, os comandos e os resultados, em vez de só as 6 últimas falas.
- "OpenAI — minha API" numa conversa que começou num modelo da conta: o raciocínio cifrado pela conta, que a API não aceita, fica de fora.
- "Usar este reset", "Configurar" e "Sair…" (menu da cota) e "Renomear" (pastas recentes) abriam uma janela invisível que travava os cliques; agora a pergunta aparece dentro do próprio menu.

## [1.5.1] - 2026-11-10 · compilação 20261111011834

### Corrigido
- Codex › Continuar pela API: o gasto final não aparece mais como US$ 0,00 quando o painel da OpenAI ainda não informou o custo. O painel mostra as requisições antes do custo e não atualiza o saldo na hora; agora o cartão diz que o custo ainda não foi informado, usa o custo do dia quando o saldo está parado e confere de novo 5, 15, 60 e 180 minutos depois do fim.

## [1.5.0] - 2026-11-10 · compilação 20261111005816

### Adicionado
- Codex › Continuar pela API (menu da cota): as conversas paradas por falta de cota continuam pela sua chave da API da OpenAI, na mesma conversa e com o mesmo modelo e nível, com gasto ao vivo e o botão Parar.
- Configurações › Modelos: o Claude no seletor do Codex pode executar comandos e editar arquivos (desligado por padrão); nas etapas longas, o Codex recebe o aviso de que ele ainda trabalha.

### Corrigido
- Com o Codex bloqueado pela conta, a cota mostra o motivo e quando volta, e a conversa parada não vira mais "Estado desconhecido".
- Fechar o componente das IAs no Codex encerra também o Claude Code e os comandos dele.
- A linha fina na base da barra não pisca mais sobre o Claude, o ChatGPT e o Edge maximizados: aparece sempre que há uma janela maximizada embaixo.

## [1.4.8] - 2026-11-10 · compilação 20261110135241

### Corrigido
- Memória: cada vez que Configurações era aberta e fechada, a janela continuava inteira na memória, cerca de 36 MB por vez, e ainda se atualizava a cada leitura das cotas do Gemini. O WinUI não libera janelas fechadas, então agora a mesma janela é reaproveitada: fechar só a esconde, e escondida ela não faz leituras. Medido: 4 aberturas deixavam 146 MB presos; agora a primeira custa cerca de 18 MB uma vez só, e as seguintes não acrescentam nada.
- Memória: os logotipos das IAs (Gemini, Copilot, OpenAI e outros) eram desenhados com 1898 × 1898 pixels, 14 MB cada, para aparecer com uns 20 pixels. Agora são desenhados no tamanho em que aparecem: só os ícones das cotas chegavam a ocupar 58 MB. No Gerenciador de Tarefas, a barra abre com cerca de 233 MB em vez de 253 MB.
- Área de transferência: a imagem de uma cópia de tela é liberada logo depois de convertida, em vez de esperar a próxima coleta de memória.

## [1.4.7] - 2026-11-10 · compilação 20261110094036

### Corrigido
- Memória: na 1.4.6, a devolução ao Windows da memória que o .NET reserva na abertura (1 minuto depois de abrir a barra) não acontecia, porque o temporizador dela era descartado antes de disparar; só a devolução ao apagar a tela funcionava. Agora as duas funcionam (medido logo depois de abrir: de 80 para 26 MB reservados).

## [1.4.6] - 2026-11-10 · compilação 20261110093029

### Corrigido
- Uso de CPU com a tela apagada ou a sessão bloqueada: a barra seguia trabalhando para ninguém. Lia os sensores a cada segundo, consultava as janelas do ChatGPT e do Claude, abria navegadores ocultos para ler cotas (a cada 5 a 6 minutos), fotografava a tela para o fundo adaptativo e animava o módulo do agente. Agora tudo isso para quando a tela da sessão apaga ou a sessão é bloqueada, e volta na hora em que a tela acende (medido com a tela apagada: de 3,9% para 0,5% de um núcleo).
- Uso de CPU enquanto uma IA trabalha: o brilho do contorno, a estrela do Claude, os logotipos girando e o círculo da tarefa agora andam num relógio único de cerca de 32 quadros por segundo. Antes, essas animações faziam a barra ser redesenhada na taxa da tela (165 vezes por segundo neste PC). O visual é o mesmo (medido com uma IA trabalhando: a barra de 15,6% para 4,5% de um núcleo, e o desenho da tela de 11,1% para 1,8%).
- Placa de vídeo: o uso e a temperatura agora vêm direto do kernel gráfico do Windows, a mesma fonte do Gerenciador de Tarefas. Antes, cada leitura do uso percorria cerca de 900 contadores (3 ms de CPU a cada 2 segundos), e a temperatura da NVIDIA vinha do nvidia-smi.exe, aberto a cada 10 segundos (44 ms de CPU por vez, e podia acordar a placa dedicada só para isso). Se a placa dedicada estiver desligada para economizar energia, a temperatura dela fica oculta até ela voltar a ser usada.
- Painel da CPU: o número de processos, threads e handles agora vem de uma chamada só do Windows. Os contadores usados antes percorriam todos os processos e threads do PC a cada segundo, gastando cerca de 1% de um núcleo só para mostrar três números.
- Etapas da tarefa: cada linha da conversa agora é interpretada uma vez só. Antes, a barra guardava na memória até 2 milhões de caracteres de cada conversa e interpretava tudo de novo a cada mudança, várias vezes por minuto enquanto a IA trabalha.
- Conversas no aplicativo do ChatGPT e do Claude: a janela do aplicativo é consultada a cada 2,5 segundos só quando ele está em uso ou tem uma conversa em andamento; fora disso, a cada 10 segundos. Cada consulta também faz o aplicativo consultado trabalhar (medido: o ChatGPT parado subia de 0,3% para 2,5% de um núcleo só por causa delas).
- IA local: com o Ollama fechado, a barra procura o servidor a cada 10 segundos, em vez de listar todos os processos do PC a cada 2 segundos.
- Temperatura pelo LibreHardwareMonitor ou pelo OpenHardwareMonitor: quando eles não estão instalados, a barra para de perguntar a cada 10 segundos (cada tentativa terminava em erro no serviço WMI).
- Memória: a barra devolve ao Windows a memória que o .NET reserva na abertura e quando a tela apaga (medido: de 100 para 21 MB reservados), e passa a usar o Segment Heap, o gerenciador de memória do Windows usado pelo Edge e pelo Chrome. No Gerenciador de Tarefas, a barra caiu de cerca de 356 para cerca de 260 MB.

## [1.4.5] - 2026-11-09 · compilação 20261110024050

### Corrigido
- Memória e disco com sessões longas do Claude Code ou do Codex: a cada mudança da conversa, a barra relia o arquivo inteiro da conversa (8,8 MB numa sessão longa, várias vezes por minuto) para acompanhar as etapas da tarefa. As linhas grandes se acumulavam na memória (cerca de 280 MB a mais) e o disco ficava sendo lido o tempo todo. Agora ela lê só o que foi acrescentado desde a última vez (medido: leitura de disco de 184 para 10 KB/s, e a barra em cerca de 350 MB em vez de 600 a 660 MB).
- Uso de CPU e placa de vídeo enquanto uma IA trabalha: o brilho colorido em volta do módulo do agente era refeito com desfoque a cada quadro da tela (165 vezes por segundo neste PC, nas duas barras) e chegava a ocupar de 18 a 35% de um núcleo e 8% da placa de vídeo. Agora o contorno desfocado é desenhado uma vez só e as cores passam por trás dele 30 vezes por segundo. O visual é o mesmo, com cerca de metade da CPU e da placa de vídeo (medido: de 17,9% para 8,3% de um núcleo e de 8,4% para 3,6% da placa).
- IA local: quando a placa de vídeo erra um cálculo (erro de CUDA como "illegal memory access" ou "misaligned address", comum com overclock acima do que a placa aguenta), a mensagem agora diz isso e sugere reduzir o overclock. Antes ela falava em falta de memória. Se o Ollama estiver reabrindo o modelo depois de cair, a barra pede para tentar de novo em alguns segundos, em vez de dizer que o serviço está instável e que "não é falha do seu PC".
- Modelos locais que pensam (como o Qwen 3.5) no Codex: o seletor mostrava só "Alto", sem escolha, porque o Codex não exibe o nível "sem raciocínio". Agora aparecem **Baixo** (sem raciocínio, mais rápido) e **Alto** (pensa antes de responder), e o Baixo de fato desliga o raciocínio. Os modelos já no seletor passam a ter os dois níveis na próxima verificação das IAs no Codex.

## [1.4.4] - 2026-11-09 · compilação 20261110010828

### Corrigido
- Uso de memória: com tudo ligado, a barra ocupava cerca de 1,7 GB, dos quais 1,3 GB eram navegadores WebView2 ocultos abertos o tempo todo (no Gerenciador de Tarefas eles aparecem como "Microsoft Edge"). Agora esses navegadores abrem só quando são usados, e a barra fica em cerca de 350 MB entre uma leitura e outra.
  - Cotas do Gemini, créditos da Microsoft e painel da OpenAI: o navegador abre para cada leitura (alguns segundos, no intervalo das cotas) e fecha em seguida. O login continua gravado. Com o menu de cotas aberto, as cotas do Gemini continuam atualizando a cada 2 segundos.
  - Fechar a janela do painel da OpenAI agora fecha de verdade, em vez de só escondê-la.
  - Conversas com o Copilot e o Gemini pelo site: a página fecha depois de 15 minutos sem uso. A próxima pergunta abre outra, com o mesmo login e com o histórico da conversa da barra.

## [1.4.3] - 2026-11-09 · compilação 20261109201912

### Corrigido
- Fechar a barra (menu da bandeja, atualização, desinstalação ou `tools\stop-bar.ps1`) às vezes não encerrava o programa: a barra sumia da tela, mas o WinStatusBar continuava rodando escondido, sem os serviços, até ser encerrado à força (na atualização, o instalador fazia isso).
  - Causa: o WinUI só termina quando a última janela do programa fecha. As leituras de cotas do Gemini e de créditos da Microsoft usam janelas ocultas do WebView2, e os timers delas continuavam ativos depois do pedido de saída e recriavam essas janelas. A última janela nunca fechava.
  - Agora esses timers param no início da saída, e o programa termina sozinho logo depois de remover a barra, o ícone da bandeja e a ponte do Codex, sem esperar o WinUI se desmontar. Antes, esse desmonte às vezes derrubava o programa com uma IA respondendo, e o relatório de erros do Windows segurava a saída por 10 a 25 segundos.
  - Limite de segurança: se a saída não terminar em 3 segundos (por exemplo, com a barra ocupada), a própria barra libera o espaço reservado no topo da tela e se encerra. O registro (`winstatusbar.log`) diz em que etapa ela estava.

## [1.4.2] - 2026-11-09 · compilação 20261109163414

### Alterado
- IAs da barra no Codex: a ligação automática acontece só onde a barra já estava instalada (na atualização, se o PC tem o Codex e a ligação nunca foi feita). Numa instalação nova, a ligação fica desligada até você clicar em "Ligar no Codex", em Configurações › Modelos.

## [1.4.1] - 2026-11-09 · compilação 20261109103623

### Corrigido
- Atualização automática: uma queda de conexão no meio do download mostra "A conexão com o GitHub caiu durante o download", em vez de um texto técnico em inglês.
- Atualização automática: o instalador já baixado e conferido é reaproveitado. Depois de 3 falhas seguidas da mesma versão, a consulta automática passa a esperar 6 horas (o botão "Verificar atualizações" continua tentando na hora). Antes, uma falha que se repetia baixava cerca de 270 MB a cada 15 minutos.
- Redes com proxy que pede o login do Windows: a atualização e o download do Microsoft Edge WebView2 passam a enviar as credenciais da conta.
- Instalador: quando a instalação falha, a etapa 3 explica o motivo em português simples (acesso negado, arquivo em uso, disco cheio) e só diz "Nada foi alterado" quando os arquivos antigos ainda estavam no lugar.

### Alterado
- Publicação: a busca de segredos confere também os commits ainda não enviados e os nomes internos, e uma versão já publicada é recusada antes de qualquer mudança.

## [1.4.0] - 2026-11-09 · compilação 20261109093522

### Adicionado
- Instalador com assistente de 3 etapas (Sobre → Instalando → Concluído) para quem abre o arquivo com dois cliques. As atualizações automáticas continuam sem nenhuma janela.
- Configurações › Atualizações (aba nova): versão instalada, botão "Verificar atualizações" com o andamento (procurando, baixando, instalando ou o motivo de uma falha) e o que mudou em cada versão.
- Instalação pelo winget, sem o aviso do SmartScreen: `winget install winstatusbar` (depois que a Microsoft aprovar o pacote).

### Alterado
- Atualizações automáticas pelas Releases do GitHub (smendesjunior/WinStatusBar), a cada 15 minutos, em silêncio e sem pedir administrador. O instalador baixado é conferido (tamanho e SHA-256) antes de rodar.
- Esta é a última versão distribuída pelo compartilhamento de rede e pela pasta do OneDrive. Os PCs que a recebem por lá passam a se atualizar pelo GitHub.
- Sobre: a linha de atualizações foi para a aba Atualizações.
- Aplicativos instalados: editor "Sidney Junior" e link para a página do projeto.

## [1.3.2] - 2026-11-09 · compilação 20261109050307

### Corrigido
- Tarefas do Codex: uma execução do `codex exec` cujo processo terminou sem gravar o fim (ex.: um teste do seletor de modelos que ficou sem resposta) deixa de aparecer como "Executando" para sempre. Depois de 2 minutos em silêncio, se não houver nenhum `codex exec` aberto que possa ser o dono dela, a barra mostra "Execução interrompida".
- Menu "Tarefa exibida na barra": parou de piscar com o menu aberto, principalmente com o mouse em cima. Antes, cada atualização (como o Claude Code mudando de etapa) recriava todas as linhas; agora só o conteúdo da linha que mudou é trocado.

## [1.3.1] - 2026-11-08 · compilação 20261109003617

### Corrigido
- Gemini e Microsoft Copilot (na barra e nas IAs do Codex): as páginas escondidas deixam de ser desaceleradas pelo navegador interno quando ficam fora da tela, e as respostas chegam mais rápido (ex.: Gemini 3.6 Flash, de 13 s para cerca de 9 s). A demora do modo "3.1 Pro" vem do próprio Gemini na conta usada pela barra.
- IAs no Codex: quando uma resposta do Gemini ou do Copilot passa de 40 s, uma foto e o estado da página ficam em `%LOCALAPPDATA%\WinStatusBar\codex-ias` (lenta-gemini.png e .json), para diagnóstico.
- IAs no Codex: um erro inesperado do encaminhador vai só para o registro, sem abrir janela de erro.
- IAs no Codex: religar ou "Corrigir problemas" sem mudança no config.toml não cria mais uma cópia de segurança a cada vez (a cópia sai só quando o arquivo muda).

### Alterado
- Caixa de mensagem do chat: o texto de exemplo mostra o que dá para pedir ("Pergunte o que quiser ou peça algo, como renomear arquivos de uma pasta").

## [1.3.0] - 2026-11-08 · compilação 20261109001225

### Adicionado
- Configurações › Modelos (aba nova):
  - "Verificar e atualizar modelos": relê, só quando você clica, os modelos de cada IA configurada e atualiza de uma vez as listas do chat da barra e o seletor de modelos do Codex. Mostra a lista das IAs detectadas no PC, cada uma com o seu estado (NO CODEX, FORA e o motivo, FALHA com "Tentar de novo", "Entrar" quando falta login) e o que entrou ou saiu.
  - Ligação com o Codex com selo de estado (ONLINE, FALHA, DESLIGADO, INDISPONÍVEL), "Ligar no Codex", "Desligar (desfazer)" e "Corrigir ligação".
  - Modelos da OpenAI possivelmente obsoletos são apontados e só saem do Codex com a sua confirmação.
- IAs da barra no seletor de modelos do Codex, dentro do pacote da barra. Substitui o app avulso "Codex IAs" e o encaminhador em Python.
  - Encaminhador próprio na porta 11435, só para o próprio PC. Continua funcionando com a barra fechada e inicia com o Windows.
  - Só entram as IAs configuradas no PC: Claude Code com conta, Gemini e Copilot com login na barra (com os modos dos sites), Lenovo Qira com a ponte local, APIs com chave na barra e modelos locais.
  - Os modelos da OpenAI continuam iguais, pela conta. Se o Codex já usava o Ollama, os pedidos continuam passando por ele.
  - Opções "OpenAI — minha API" pela chave da API.
  - Modelos novos do Ollama entram no seletor do Codex na verificação, e os apagados saem.
  - Na primeira vez que esta versão roda num PC com o Codex, a ligação é feita sozinha, com cópia de segurança do config.toml. Quem desligar depois continua desligado.
- "Corrigir problemas das IAs" (menu "…" da barra e Configurações › Modelos): confere e conserta sozinho o que as IAs da barra e do Codex precisam:
  - o encaminhador parado ou travado, ou a porta ocupada pelo encaminhador antigo;
  - o config.toml alterado por outro programa, as IAs fora do seletor e o início com o Windows;
  - a ponte antiga da barra, o Ollama parado e o Lenovo Qira travado ou sem o serviço de modelos;
  - a ligação do Claude Code com a barra e as integrações da barra.
  No fim, verifica os modelos. O que depende de você (login, ponte do Qira com administrador) aparece com o botão certo. "Refazer todas as ligações" refaz tudo mesmo sem erro detectado.
- Gemini e Copilot no chat da barra: os modos do seletor de cada site (ex.: "Pro", "Think Deeper", submenu "GPT") aparecem na lista e são escolhidos no site antes do envio.

### Alterado
- Claude: a lista de modelos vem do próprio Claude Code e é a mesma para a barra e o Codex; muda só na verificação.
- Modelos da OpenAI pela API: os níveis de pensamento vêm da lista do Codex instalado, atualizada na verificação.
- A lista "Codex" do chat da barra não mostra mais as IAs da própria barra que estão no seletor do Codex.
- Instalador:
  - na atualização, encerra o encaminhador do Codex antes de trocar os arquivos (a barra o inicia de novo);
  - na desinstalação, devolve o config.toml do Codex de antes.

### Corrigido
- Lenovo Qira:
  - logo depois de um reinício do Qira, a barra dizia que não tinha acesso; agora espera a ponte local por até 45 s e diz se ela está ativa;
  - o reinício do Qira não é mais interrompido no meio quando a pergunta é cancelada.

## [1.2.1] - 2026-11-08 · compilação 20261108215409

### Corrigido
- Lenovo Qira: parar a pergunta enquanto ela espera a vez do Codex IAs mostrava "ocupado por mais de 5 minutos"; agora mostra "Interrompido", como nos demais casos.

## [1.2.0] - 2026-11-08 · compilação 20261108213933

### Alterado
- Lenovo Qira compartilhado com o Codex IAs (`Aplicativos IA\Codex IAs`), o app que leva Claude, Gemini, Copilot e Qira ao seletor de modelos do Codex sem depender da barra:
  - uma pergunta por vez entre a barra e o Codex IAs: se o Codex estiver usando o Qira, a barra mostra "aguardando a vez" e envia logo depois;
  - a leitura dos créditos e a conferência antecipada do Qira ficam para depois quando o Codex IAs está usando o Qira;
  - o reinício completo do Qira (quando o núcleo trava) respeita o intervalo de 10 minutos contando também os reinícios feitos pelo Codex IAs.
- Sem o Codex IAs instalado, nada muda.

## [1.1.0] - 2026-11-08 · compilação 20261108210420

### Adicionado
- Fundo adaptativo: com uma janela na metade da tela, ou duas lado a lado encostadas no topo, a barra fica na cor sólida do tema do Windows.
- Fundo adaptativo, barra transparente: cada parte da barra escolhe texto e ícones claros ou escuros pelo papel de parede logo atrás dela, pelo critério de contraste de acessibilidade (WCAG).
  - Os botões com fundo próprio (a pílula do agente e "Pergunte à sua IA") continuam no tema do Windows.

### Alterado
- Fundo adaptativo: Mica, Mica Alt ou Acrílico acompanham o estado ativo ou inativo da janela sem serem recriados.
  - A transição é a mesma do Windows.
  - As cores sólidas entram por cima com transição suave.
  - A troca da base acontece coberta, sem quadro vazio, e a barra não pisca mais.
- Fundo adaptativo mais leve:
  - a leitura roda fora da thread da interface, com uma captura de tela por leitura;
  - o nome do processo só é buscado ao gravar o log;
  - ao arrastar janelas, há no máximo uma leitura a cada 100 ms;
  - eventos de outros monitores são ignorados;
  - a verificação de segurança passa de 1 s para 3 s quando nada muda.
- Fundo, texto e ícones acompanham em tempo real as trocas de janela e o papel de parede:
  - ao minimizar, restaurar, mostrar a área de trabalho ou trocar de janela, a barra lê em rajada, a cada 80 ms, durante a animação do Windows;
  - quando o papel de parede muda, a leitura é imediata;
  - com a barra transparente, a conferência roda a cada 1 s.
- Troca imediata ao voltar para uma janela já vista, porque a aparência dela fica memorizada.
  - Trocar entre material e cor sólida exige duas leituras concordando.
- Traduções:
  - 392 textos novos em inglês, espanhol, francês, japonês e chinês;
  - 133 traduções automáticas antigas corrigidas. "Cotas do Claude" aparecia como "ações do Claude", "Codex" como "Códice" e "法典", e "Mica Alt" como "Alt Mica".

### Corrigido
- Uma janela pequena na frente da barra de título (miniplayer, notificação, diálogo) contaminava a cor da barra.
- Com "Mostrar cor de destaque nas barras de título" ligada, a barra podia mostrar Mica em vez da cor de destaque.
- Uma janela no tamanho normal quase do tamanho da tela, encostada no topo, agora é tratada como maximizada.
- Mensagens do Copilot, do Gemini e do Qira agora aparecem no idioma do app.

## [1.0.0] - até 2026-11-08 · compilações até 20261108200604

A versão inicial cobre todas as compilações entregues até 08/11/2026. As últimas trouxeram:
- troca de IA, modelo e nível no meio da conversa sem perder o contexto, com a linha "IA alterada de … para …", como no Codex;
- erros de todas as IAs explicados em português, com o detalhe técnico à parte;
- IA local com modelo só de texto (por exemplo, phi4-mini): a captura da janela vai como texto reconhecido pelo OCR do Windows, o que corrigiu o "HTTP 400";
- lista de modelos da IA local relida do Ollama a cada abertura do menu.
