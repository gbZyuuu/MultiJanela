using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class FeatureGuidePanel : System.Windows.Controls.UserControl
{
    private sealed record GuideRow(string Title, string Description);
    private bool _subscribed;
    public FeatureGuidePanel()
    {
        InitializeComponent(); Refresh();
        Loaded += (_, _) => { if (!_subscribed) { L.LanguageChanged += LanguageChanged; _subscribed = true; } };
        Unloaded += (_, _) => { if (_subscribed) { L.LanguageChanged -= LanguageChanged; _subscribed = false; } };
    }
    private void LanguageChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Refresh);
    private void Refresh() => Rows.ItemsSource = new[]
    {
        new GuideRow(L.T("Painel"), L.T("Mostra sessões em execução e contas salvas separadamente. PID identifica o processo; versão identifica o cliente aberto. Organizar distribui as janelas no monitor escolhido.")),
        new GuideRow(L.T("Abrir"), L.T("Escolha contas, ordene a sequência e informe um jogo, PlaceId ou servidor privado. Abrir no destino utiliza o conteúdo atual do campo; Abrir na home inicia sem destino específico.")),
        new GuideRow(L.T("Jogos"), L.T("All exibe a descoberta pública do Roblox. Mais jogados usa a lista de jogadores ativos. A busca consulta o Roblox, e um PlaceId ou link pode abrir um jogo diretamente. Jogos privados ou indisponíveis continuam sujeitos às permissões do Roblox.")),
        new GuideRow(L.T("Servidores"), L.T("Procura servidores públicos com espaço para as contas escolhidas. A lista pode ser ordenada por ocupação. Um ID escolhe um servidor específico; a vaga não fica reservada até a entrada.")),
        new GuideRow(L.T("Destinos salvos"), L.T("Guarda links com nomes editáveis. Selecionar preenche o destino. Alterar o campo cria um novo rascunho sem modificar o link salvo.")),
        new GuideRow(L.T("Perfis de abertura"), L.T("Salva contas na ordem, destino, intervalo, CPU e preferências de organização. Carregar prepara a tela Abrir; só inicia quando você manda abrir.")),
        new GuideRow(L.T("Grupos de contas"), L.T("Salva apenas um conjunto de contas. Serve para filtrar a biblioteca ou preencher rapidamente a sequência, sem trocar o destino e as preferências.")),
        new GuideRow(L.T("Notas e etiquetas"), L.T("São informações locais para organizar contas e facilitar a busca. Campos personalizados guardam pares nome=valor definidos por você.")),
        new GuideRow(L.T("Contas"), L.T("Amigos, visuais, inventário, corpo, cores e comunidades usam a interface do gerenciador. A prévia 3D não altera a conta; use Aplicar para confirmar. A geração depende do Roblox. Segurança e verificações usam páginas oficiais.")),
        new GuideRow(L.T("Entrada rápida"), L.T("Autoriza outro dispositivo sem digitar a senha nele. Gere o código no dispositivo novo e confirme esse código na conta já conectada. O recurso é do próprio Roblox.")),
        new GuideRow(L.T("CPU global"), L.T("Define um teto separado para cada processo. Cada conta herda o global até receber um valor próprio. 100% remove o teto do gerenciador; não é a leitura do uso atual.")),
        new GuideRow(L.T("Gráficos de abertura"), L.T("Salva FPS e qualidade globais ou específicos de uma conta. Aplica antes de iniciar e tenta devolver o arquivo anterior depois. O arquivo nativo é compartilhado; não é controle independente ao vivo.")),
        new GuideRow(L.T("RAM e VRAM"), L.T("Mostram consumo medido pelo Windows. Liberar memória de trabalho devolve páginas ao sistema, que pode carregá-las novamente. Isso não estabelece um teto rígido de RAM ou VRAM.")),
        new GuideRow(L.T("Ajustes de processo"), L.T("Prioridade e EcoQoS são opções de agendamento e energia do Windows. Podem ser aplicados apenas em segundo plano; desativar devolve as preferências anteriores quando possível.")),
        new GuideRow(L.T("Reconectar após queda"), L.T("Tenta reabrir uma sessão depois de uma falha reconhecida. Preserva o destino original e tenta o mesmo servidor quando há identificação confiável. Fechamentos pedidos por você não provocam reconexão.")),
        new GuideRow(L.T("Agendamentos e automações"), L.T("Executa perfis e ações em horários definidos enquanto o gerenciador está aberto. Reabrir fecha e inicia as contas do perfil, com permissão explícita. Receitas combinam passos limitados; não executam código dentro do Roblox.")),
        new GuideRow(L.T("Notificações"), L.T("Avisa sobre eventos na área de trabalho e, opcionalmente, em um webhook. O webhook recebe tipo de evento e quantidade de sessões, sem links privados ou cookies.")),
        new GuideRow(L.T("Backup e restauração"), L.T("Exporta dados em um arquivo criptografado com senha. Restaurar cria uma cópia de segurança e reinicia o gerenciador. Perfis de navegador podem depender deste Windows.")),
        new GuideRow(L.T("Integrações e API local"), L.T("Permite que ferramentas deste computador consultem o gerenciador. Ações exigem permissões separadas e uma chave privada. Vem desligada e não publica um servidor na internet.")),
        new GuideRow(L.T("Versões do Roblox"), L.T("Consulta deployments publicados, baixa pacotes oficiais, verifica a assinatura do cliente e permite escolher uma versão instalada. Trocar a versão exige terminar a fila e fechar os jogos. Uma versão antiga pode ser recusada pelo Roblox.")),
        new GuideRow(L.T("Aparência e acessibilidade"), L.T("Escolha tema, cor, fonte e tamanho de texto. O modo de gravação oculta nomes e destinos privados. Idioma e animações ficam na categoria Geral; o idioma muda imediatamente.")),
        new GuideRow(L.T("Lotes"),L.T("Selecione as contas acima, escolha a ação e confira a prévia antes de executar.")+" "+L.T("Verificações são concluídas nas páginas oficiais. Um pedido enviado não significa amizade aceita ou ingresso aprovado.")),
        new GuideRow(L.T("Avatares"),L.T("Monte e distribua um visual")+". "+L.T("Somente itens oficiais gratuitos podem ser obtidos.")),
        new GuideRow(L.T("Gravações e TinyTask"),L.T("A reprodução usa uma janela por vez. Mudar o foco interrompe a entrada; Parar solta as teclas pressionadas.")),
        new GuideRow(L.T("Áudio"),L.T("Somente processos abertos e identificados pelo gerenciador recebem alterações. Desativar restaura os volumes anteriores quando eles não foram alterados por outro programa.")),
        new GuideRow(L.T("Versão por conta"),L.T("O catálogo global continua disponível no botão Versões. A escolha individual é preservada para novas aberturas e reconexões; uma versão antiga pode ser recusada pelo Roblox.")),
        new GuideRow(L.T("Cofre e importação"),L.T("O cofre criptografa os arquivos JSON do gerenciador. Perfis de navegador continuam usando a proteção do Windows; logs não fazem parte do cofre.")),
        new GuideRow(L.T("Nexus"),L.T("Conexões locais autenticadas. O cliente deve informar nome, ID Roblox e a chave. Nenhum código é injetado pelo gerenciador.")),
        new GuideRow(L.T("Scripts"),L.T("Ambiente isolado, sem rede, arquivos ou sessões do navegador. Tempo máximo de 30 segundos. API: ram.accounts(), ram.log(), ram.wait(), ram.arrange() e ram.nexus().")),
        new GuideRow(L.T("Diagnóstico"), L.T("Reúne informações do gerenciador, cliente e processos para ajudar a investigar falhas. O reparo de sessão local cria um backup e só pode ser feito com o Roblox fechado. Não substitui verificações da conta.")),
        new GuideRow(L.T("Comandos e atalhos"), L.T("Ctrl+K abre a busca de comandos. Ctrl+1 até Ctrl+5 muda de página; Ctrl+, abre configurações. Esc fecha painéis. A pesquisa de contas aceita filtros como tag:, group:, user: e id:.")),
        new GuideRow(L.T("Histórico"), L.T("Registra eventos de sessão e amostras recentes de recursos com limites de retenção. Exportar CSV oculta nomes por padrão. Leituras indisponíveis aparecem como um traço.")),
        new GuideRow(L.T("Atualizações e instalação"), L.T("Consulta releases publicados, verifica o download e prepara uma versão nova. A instalação opcional cria atalhos para o usuário atual. Desinstalar preserva as contas salvas."))
    };
}
