# Registro de mudanças do WinStatusBar

As mudanças relevantes de cada versão entregue. O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e as versões seguem o [versionamento semântico](https://semver.org/lang/pt-BR/):
- **PATCH** para correções;
- **MINOR** para novidades compatíveis;
- **MAJOR** para mudanças incompatíveis.

A compilação (data e hora UTC) é o que o atualizador compara. A versão é para as pessoas.

## [1.4.0] - 2026-11-09 · compilação 20261109093522

### Adicionado
- Instalador com assistente de 3 etapas (Sobre → Instalando → Concluído) para quem abre o arquivo com dois cliques. As atualizações automáticas continuam sem nenhuma janela.
- Configurações › Atualizações (aba nova): versão instalada, botão "Verificar atualizações" com o andamento (procurando, baixando, instalando ou o motivo de uma falha) e o que mudou em cada versão.
- Instalação pelo winget, sem o aviso do SmartScreen: `winget install SidneyJunior.WinStatusBar` (depois que a Microsoft aprovar o pacote).

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
