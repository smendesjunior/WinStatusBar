# Encaminhador do Codex: agora dentro da barra

Desde a versão 1.3.0 (08/11/2026), o encaminhador do Codex (porta 11435) faz parte do pacote da WinStatusBar.

- **Componente:** `src\WinStatusBar.CodexIAs`, escrito em C#. É publicado como `Automation\WinStatusBar.CodexIAs.exe` e vem junto com a barra em cada atualização.
- **Substitui:**
  - o `gateway.py`, que ficava em `%LOCALAPPDATA%\OpenAI\CodexApiGateway`;
  - o app avulso `Aplicativos IA\Codex IAs`.
- **Como usar:** na barra, em Configurações › Modelos:
  - **Ligar no Codex** põe as IAs configuradas no seletor do Codex, com cópia de segurança do `config.toml`;
  - **Desligar (desfazer)** devolve a configuração de antes;
  - **Verificar e atualizar modelos** relê os modelos.

## Rotas

| Modelo pedido pelo Codex | Para onde vai |
|---|---|
| `codexias-*` | IAs configuradas na barra (Claude Code, Gemini e Copilot pelos sites, Lenovo Qira, APIs com chave, modelos locais), respondidas pelo próprio componente |
| `openai-minha-api-*` | API pública da OpenAI, pela chave em `%LOCALAPPDATA%\OpenAI\CodexApiGateway\openai-key.dpapi` (cadastrada por `Cadastrar-chave-OpenAI.ps1`) ou por uma API própria da OpenAI cadastrada na barra |
| `winstatusbar-*` | Ponte antiga da barra (porta 11437), só para conversas antigas do Codex |
| Qualquer outro (modelos da conta, modelos do Ollama) | O destino que o Codex já usava: o Ollama (`/api/codex/v1`) neste PC, ou a OpenAI direto |

## Arquivos

- **Estado da ligação, relatório e listas:** `%LOCALAPPDATA%\WinStatusBar\codex-ias` (`integracao.json`, `relatorio.json`, `modelos.json`, `modos.json`, `codex-ias.log`).
- **Início com o Windows:** `HKCU\...\Run\WinStatusBar.CodexIAs`. Existe só com a ligação ligada; a barra também o inicia ao abrir.
- **Ollama:** os modelos novos entram no seletor do Codex pela verificação (catálogo e `~/.codex/ollama-launch-codex-routing.json`), e os apagados do Ollama saem.

Não reinstale o `gateway.py`: ele disputaria a porta 11435 com o componente.
