# Testes interativos do executor

Execute da raiz do projeto, após compilar WinStatusBar.Automation em Release:

- `dotnet run --project tools/DesktopControl.Smoke -c Release`: formulário descartável sem título; testa descoberta, digitação Unicode e leitura do valor.
- `dotnet run --project tools/DesktopControl.Smoke -c Release -- --shell-only`: abre a pesquisa do Windows, preenche consulta de teste, confere e fecha com Escape. O seletor do campo usa o nome português Pesquisar.
- `dotnet run --project tools/DesktopControl.Smoke -c Release -- --agent`: usa a sessão autenticada do Codex para traduzir Olá, mundo! e inserir Hello, world!, sem Enter, verificando UIA. Consome uma chamada de IA por etapa.

São testes opt-in, fora da suíte/CI: alteram foco e consulta de pesquisa. Não execute enquanto alguém digita; entrada manual pode revogar a autorização de ativação da janela e o teste deve interromper. Não simule sucesso quando houver essa recusa. O harness não usa documentos do usuário. Permissões, autenticação/UAC e controle de dispositivos não são testados aqui.
