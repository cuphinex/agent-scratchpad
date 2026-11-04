# Agent Scratchpad

Um scratchpad para Windows 11, ao estilo do Omarchy, para agentes de linha de comandos. **Win+S** faz deslizar, a partir de uma borda, uma janela flutuante com os teus agentes (por omissão, Claude Code; o Grok e outros abrem-se em painéis ao lado). A janela segue o design do Windows 11: fundo desfocado, cantos arredondados, sem barra nem bordas, e o resto do ecrã escurece ligeiramente. Outro Win+S esconde-a e as sessões continuam vivas.

A app não usa o Windows Terminal: tem um terminal próprio, que corre o agente no ConPTY do Windows e desenha o texto diretamente sobre o acrylic.

## Instalar / atualizar
```
powershell -ExecutionPolicy Bypass -File .\build.ps1
```
O script:
- compila com o `csc.exe` que vem com o Windows (não é preciso instalar nada);
- instala em `%LOCALAPPDATA%\AgentScratchpad` e, se encontrar o antigo `Scratchpad`, traz a configuração dele e remove-o;
- cria o atalho de arranque com o Windows;
- arranca a app, que fica com um ícone `>_` na bandeja.

## Usar
- **Win+S** mostra/esconde. Também esconde ao clicar na zona escura ou ao mudar de app (`hide_on_blur`).
- Painéis lado a lado (só com o scratchpad aberto):
  - **Win+Enter** abre o menu de agentes; com 1–9 ou Enter abres um painel novo à direita, e a janela alarga para `width_split`.
  - **Win+W** fecha o painel ativo; se era o último, o scratchpad esconde-se.
  - **Win+← / Win+→** ou um clique mudam o painel ativo; os outros ficam esbatidos.
  - Até 4 painéis. Todos continuam vivos quando escondes o scratchpad.
- **Shift+Enter** nova linha · **Shift+Tab** muda o modo do Claude Code · **Alt+V** cola imagens.
- **Ctrl+C** copia se houver seleção (senão interrompe) · **Ctrl+V** cola texto · botão direito copia/cola.
- Arrastar seleciona · duplo clique seleciona a palavra · roda ou **Shift+PgUp/PgDn** percorrem o histórico.
- `/exit` fecha a sessão; o Win+S seguinte abre uma nova. Na bandeja: *Nova sessão*, *Abrir configuração*, *Sair*.

## Configurar
`%LOCALAPPDATA%\AgentScratchpad\config.ini` (bandeja → *Abrir configuração*). Gravar aplica logo; os comandos dos agentes mudam na próxima sessão.

| Chave | O que faz |
|---|---|
| `agent` (várias linhas) | `nome \| comando`, por ordem: o primeiro abre no Win+S e os outros ficam no menu do Win+Enter |
| `workdir` | pasta inicial dos agentes |
| `width_split` | largura com 2 ou mais painéis |
| `edge` | `top`, `bottom`, `left`, `right`: de onde desliza e onde fica encostada |
| `width`, `height`, `margin` | tamanho (fração do ecrã) e espaço até à borda |
| `animation_ms`, `dim` | duração do deslizar e quanto escurece o resto |
| `backdrop`, `tint` | `blur` (por omissão: fotografia desfocada do ecrã, estável ao deslizar), `acrylic` (o do Windows, que clareia enquanto a janela se move), `mica`, `tabbed`, `none`; e escurecimento do próprio fundo |
| `transparent_bg` | `auto` (por omissão): se um agente pinta o próprio fundo (ex.: o Grok, quase preto), essa cor fica transparente; `off` mostra-a |
| `corners`, `border` | `round`/`small`/`square` e linha à volta (`none`, `default`, `#RRGGBB`) |
| `font`, `font_size`, `padding` | letra (a primeira que existir), tamanho e margem do texto |
| `hide_on_blur` | esconder ao clicar fora ou mudar de app |

## Grok
Já está no menu (`agent=Grok | grok`). Para o instalar (Grok Build, da xAI), numa janela do PowerShell:
```
irm https://x.ai/cli/install.ps1 | iex
```
Depois corre `grok` uma vez num terminal, para fazeres o login no browser. Precisa de uma subscrição xAI elegível. A app encontra-o sem reiniciar.

## Notas
- A janela abre no monitor onde está o rato. Numa borda partilhada com outro monitor, o painel encolhe ao monitor enquanto desliza, para não aparecer no ecrã vizinho, e o texto só surge quando chega ao sítio.
- O Win+S chega à app como Win+F23 (ou outra tecla F livre), registado com `RegisterHotKey`. É isso que dá à app o direito de tomar o foco.
- Quando uma app em modo administrador está em primeiro plano, o Windows não deixa a app ver o Win+S.
- Os emojis aparecem a uma cor (limitação do WPF).
- Erros ficam em `%LOCALAPPDATA%\AgentScratchpad\scratchpad.log`.

## Desinstalar
```
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```
