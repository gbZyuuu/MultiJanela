# MultiJanela · Quick guide / Guia rápido

## English

### Start and sign in

Extract the ZIP and run `RobloxMultiManager.exe`. Requires Windows 10/11 x64 and Microsoft Edge WebView2 Runtime; .NET is included. Use **Add account**, sign in through the official Roblox page, complete its verification and choose **Finish sign-in**. The manager does not ask for your password directly.

The sidebar opens Dashboard, Open, Games, Accounts, Collections, Tools and Settings. It shows icons in a narrow window; hovering expands the labels, and leaving collapses them. Ctrl+K opens commands and Ctrl+F searches accounts. English and Portuguese change immediately in Settings. Text size, themes and recording mode are under Appearance.

### Accounts and sessions

The dashboard keeps saved accounts and open sessions separate. Account rows offer **Open** and individual CPU/FPS/quality preferences. The account menu contains sign-in refresh, renaming, ordering and removal. Session rows show PID, version, uptime, RAM and VRAM, with selection, focus and close actions. **Arrange all**, **Close selected** and **Close all** stay above the lists.

The global CPU slider applies a separate cap to each process; 100% means no cap. Individual settings inherit the global value until edited. FPS and quality are preferences staged on launch when enabled, not independent live limits; Roblox acceptance varies. RAM and VRAM display Windows readings. They are not limited.

### Launch and games

In **Open** in the sidebar, add accounts, reorder them and paste a PlaceId, game link or private-server invitation. **Open at destination** uses that field. Selecting a saved destination fills it; editing the field detaches the saved selection. Save named links for reuse.

**Games** loads discovery automatically and supports search, favorites and recent games. Open a game to see its image and servers in a side panel. Servers load automatically; Refresh reloads them and Load more continues the list. If Roblox limits requests, the last loaded servers remain visible and the app honors the retry delay instead of retrying automatically. Choosing a server fills the launch destination. Access and capacity can change.

**Collections** saves groups, launch presets, notes and tags. Groups save accounts; presets also save launch choices. **Accounts** displays friends and saved outfits in the manager’s own interface. Outfits have thumbnails and a preview; **Apply items** changes the selected account’s equipped items after confirmation, preserving body colors, scale and body type. No items are purchased. Open games may require respawning or rejoining. Online friends in experiences appear first, with game art/name and Play for the selected account. Manage friends supports verified removal and Roblox nicknames. **Avatar editor** includes inventory categories, body colors, proportions, R6/R15 and a rotatable 3D draft preview. Preview generation requires Roblox and may be delayed or unavailable; rotating a loaded model is local. Previewing never applies changes. Communities are native; security and verification continue to use official Roblox pages. Avatar pictures are refreshed after confirmed changes, but Roblox may generate them later.

Native communities support search, details, membership, members and roles. Accounts with the required permissions can manage join requests, member roles, removals, shouts and descriptions. Other Roblox community administration features are not included. Avatar previews use full-body thumbnails and 3D models when Roblox makes them available; a static preview does not show unapplied edits.

### Tools and settings

Tools contains reviewed friendship/community batches, avatar asset sets, compatible TinyTask playback, permitted player/subplace queries, client audio, per-account client versions, vault/import, Nexus and JavaScript. Batch operations show per-account results and stop on rate limits; security challenges remain manual. Avatar tools do not buy paid items. Macros require the selected Roblox window to keep focus.

Nexus is an optional authenticated local WebSocket bridge for compatible clients, not an executor. JavaScript runs in an isolated, network-blocked browser process with a limited manager API, confirmation and a 30-second timeout. It does not run inside Roblox.

Settings contains crash reconnection, monitor arrangement, notifications, schedules, backups, updates and optional local API. Schedules require the manager to remain open. Reconnection attempts the previous destination; the same server is not guaranteed. Closing with X keeps the app in the tray; Exit stops it and releases CPU caps.

### Features and limits

| Feature | Behavior |
| --- | --- |
| Local API | Real loopback HTTP service; off by default, requires a key and separate action permissions. |
| Macros | Real keyboard/mouse playback, one identified foreground window at a time. Imports compatible TinyTask records; unsupported events are rejected. |
| JavaScript | Runs inside a separate sandbox with a limited manager API and a 30-second limit; it does not run inside Roblox. |
| Nexus | Authenticated transport for an external compatible client; no game-side client or executor is included. |
| Avatar editor | Categories, owned items, colors, proportions, R6/R15 and 3D drafts. Apply explicitly confirms account changes. |
| FPS / graphics | Launch preferences, not independent live caps. RAM and VRAM are readings only. |
| Roblox actions | Subject to Roblox permissions, availability, verification and rate limits. An implemented feature is not a guarantee that every account can use it. |

Check for updates stays in the header and shows its result. The download icon appears only for a newer published stable release. Click it to download, verify and open the new version, transferring the manager session without closing games. Unsaved work or active operations can defer the transfer. Installed copies update their managed installation; portable copies open the verified version from the update cache. A local preview newer than the published release is not considered outdated.

### Privacy and troubleshooting

Each account has a separate WebView2 browser profile. Authentication requires session data; the app is not cookie-free. Manager JSON can be protected with the optional password vault and inactivity lock. Browser profiles retain Windows protection, while logs are outside the vault. Do not share the data folder or backups. Recording mode masks manager names and destinations, not every external page; review recordings before posting.

Browser-profile isolation does not create separate devices or prevent Roblox from associating accounts. This unofficial app has no Roblox approval or guarantee against moderation. Follow the platform's and each experience's rules.

For an expired session, use **Login / refresh session**. If the native app works but website launches return 403, close Roblox and Studio and use **Settings → Repair local Roblox session**. This explicit action backs up the native session file and may require signing in again; it does not erase manager accounts or guarantee a fix for every 403.

Download only from the project's GitHub releases. The portable package includes checksums and is not code-signed. Keep backup passwords safe; they cannot be recovered.

## Português

### Começar e entrar

Extraia o ZIP e execute `RobloxMultiManager.exe`. Requer Windows 10/11 x64 e Microsoft Edge WebView2; o .NET acompanha o aplicativo. Use **Adicionar conta**, entre pela página oficial, conclua a verificação e clique em **Concluir login**. O gerenciador não solicita sua senha diretamente.

A navegação lateral abre Painel, Abrir, Jogos, Contas, Coleções, Ferramentas e Configurações. Em janelas pequenas, mostra ícones; o menu expande com o mouse e recolhe ao sair. Ctrl+K abre comandos e Ctrl+F busca contas. O idioma muda imediatamente nas configurações. Aparência oferece nove temas, tamanho do texto e modo de gravação. Preferências locais são aplicadas ao mudar e salvas automaticamente.

### Contas e sessões

O painel separa contas salvas e sessões abertas. Cada conta tem **Abrir** e preferências individuais de CPU/FPS/qualidade. O menu oferece atualização de login, nome, ordem e remoção. Cada sessão mostra PID, versão, tempo, RAM e VRAM, com seleção, foco e fechamento. **Organizar tudo**, **Fechar selecionadas** e **Fechar todas** ficam acima das listas.

O slider global limita cada processo separadamente; 100% significa sem teto. A conta herda o global até ser alterada. FPS e qualidade são preferências aplicadas na abertura quando habilitadas, não limites individuais em tempo real; dependem do Roblox. RAM e VRAM são leituras do Windows, sem limitador.

### Abrir e escolher jogos

Em **Abrir**, adicione contas, ordene e cole PlaceId, link de jogo ou convite privado. **Abrir no destino** usa esse campo. Selecionar um destino salvo preenche o campo; editá-lo desfaz a seleção. Salve links com nomes para reutilizar.

**Jogos** carrega a descoberta automaticamente e oferece busca, favoritos e recentes. Clique em um jogo para abrir imagem e servidores na lateral. Os servidores carregam automaticamente; **Atualizar** refaz a consulta e **Carregar mais** continua a lista. Se o Roblox limitar as consultas, os resultados carregados são mantidos e o aplicativo respeita o prazo de espera, sem repetir requisições automaticamente. Escolher um servidor preenche o destino da sequência. Acesso e vagas podem mudar.

**Coleções** salva grupos, perfis de abertura, notas e tags. Grupos guardam contas; perfis também guardam escolhas de abertura. **Contas** mostra amigos e visuais salvos na interface do gerenciador. Visuais têm miniaturas e prévia; **Aplicar itens** altera os itens equipados da conta selecionada após confirmação, preservando cores, escala e tipo do corpo. Nenhum item é comprado. Jogos abertos podem exigir renascer ou entrar novamente. Amigos em experiências aparecem primeiro, com imagem/nome do jogo e Play para a conta selecionada. Gerenciar amigos oferece remoção e apelidos no Roblox, com verificação. O editor de avatar inclui categorias do inventário, cores, proporções, R6/R15 e prévia 3D girável. A geração da prévia depende do Roblox e pode demorar ou ficar indisponível; girar um modelo carregado é local. Experimentar não aplica alterações. Comunidades usa uma tela nativa; segurança e verificações continuam nas páginas oficiais. A foto é atualizada após alterações confirmadas, mas sua geração depende do Roblox.

Comunidades nativas incluem busca, detalhes, participação, membros e cargos. Contas com as permissões necessárias podem gerenciar solicitações, cargos de membros, remoções, avisos e descrições. Outras funções administrativas do Roblox não estão incluídas. A prévia usa corpo inteiro e modelos 3D quando disponibilizados pelo Roblox; a imagem estática não mostra alterações pendentes.

### Ferramentas e configurações

Ferramentas reúne lotes de amizades/comunidades, conjuntos de avatar, reprodução TinyTask compatível, consultas permitidas de jogadores/subplaces, áudio, versão por conta, cofre/importação, Nexus e JavaScript. Lotes exigem revisão, mostram resultados por conta e param em limites de requisição. Verificações de segurança são manuais. Avatares não compram itens pagos. Macros exigem foco na janela selecionada.

Nexus é uma ponte WebSocket local autenticada para clientes compatíveis, não um executor. JavaScript roda em processo de navegador isolado, sem rede, com API limitada do gerenciador, confirmação e prazo de 30 segundos. Não roda dentro do Roblox.

As configurações incluem reconexão, monitores, notificações, agendamentos, backups, atualizações e API local opcional. Agendamentos exigem o app aberto. A reconexão tenta o destino anterior, sem garantir o mesmo servidor. X mantém o app na bandeja; **Sair** encerra e libera os tetos de CPU.

### Recursos e limites

| Recurso | Comportamento |
| --- | --- |
| API local | Servidor HTTP real neste computador; desligado por padrão, exige chave e permissões separadas. |
| Macros | Reproduzem teclado/mouse em uma janela identificada por vez, com foco. TinyTask tem compatibilidade parcial; eventos não suportados são recusados. |
| JavaScript | Roda em processo isolado com API limitada do gerenciador e prazo de 30 segundos; não roda dentro do Roblox. |
| Nexus | Ponte autenticada para um cliente externo compatível; não inclui cliente no jogo nem executor. |
| Editor de avatar | Categorias, inventário, cores, proporções, R6/R15 e prévia 3D. Aplicar confirma explicitamente as alterações na conta. |
| FPS / gráficos | Preferências para a próxima abertura, sem teto individual em tempo real. RAM e VRAM são apenas leituras. |
| Ações Roblox | Dependem de permissões, disponibilidade, verificações e limites da plataforma. Implementação não garante acesso para toda conta. |

Verificar atualizações fica no cabeçalho e mostra o resultado. O ícone de download aparece apenas para uma versão estável publicada mais nova. Ao clicar, o app baixa, verifica e abre a nova versão, transferindo a sessão do gerenciador sem fechar os jogos. Operações ou alterações pendentes podem adiar a troca. Cópias instaladas atualizam a instalação; cópias portáteis abrem a versão validada no cache de atualizações. Uma prévia local mais nova não é considerada desatualizada.

### Privacidade e ajuda

Cada conta usa um perfil WebView2 separado. O login precisa de dados de sessão; o app não funciona sem cookies. O cofre opcional protege JSONs do gerenciador com senha e bloqueio por inatividade. Os perfis de navegador mantêm a proteção do Windows; logs ficam fora do cofre. Não compartilhe a pasta de dados ou backups. O modo de gravação oculta nomes e destinos no gerenciador, mas não todas as páginas externas; revise antes de publicar.

Perfis separados não criam dispositivos diferentes nem impedem associação de contas pelo Roblox. O aplicativo é independente, sem aprovação do Roblox ou garantia contra punições. Siga as regras da plataforma e de cada experiência.

Para sessão expirada, use **Login / atualizar sessão**. Se o aplicativo nativo funciona, mas aberturas pelo site retornam 403, feche Roblox e Studio e use **Configurações → Reparar sessão local do Roblox**. A ação explícita guarda uma cópia do arquivo nativo e pode exigir novo login; preserva as contas do gerenciador e não resolve todo tipo de 403.

Baixe pelas releases oficiais do projeto no GitHub. O pacote inclui checksums e não tem assinatura de código. Guarde as senhas dos backups; elas não podem ser recuperadas.
