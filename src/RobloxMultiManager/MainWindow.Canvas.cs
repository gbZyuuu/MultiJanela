using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private void CanvasSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CanvasSelectionText is null) return;
        var count = InstancesGrid.SelectedItems.Count;
        CanvasSelectionText.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CanvasSelectionText.Text = count == 0 ? RobloxMultiManager.Core.L.T("Nenhuma sessão selecionada") : RobloxMultiManager.Core.L.F("{0} sessão(ões) selecionada(s)",count);
        CanvasCloseSelectedButton.IsEnabled = count > 0;
        ScheduleWorkspaceRefresh();
        CanvasCloseAllButton.IsEnabled = Instances.Count > 0;
        CanvasArrangeAllButton.IsEnabled = Instances.Any(instance => instance.HasWindow);
    }
    private void CanvasSelectAll_Click(object sender, RoutedEventArgs e) => InstancesGrid.SelectAll();
    private void CanvasClearSelection_Click(object sender, RoutedEventArgs e) => InstancesGrid.UnselectAll();

    // Always resolve a rendered card against the latest snapshot by full identity.
    // Never target another process just because Windows reused its PID.
    private RobloxInstanceInfo? CurrentCanvasSession(object sender) => sender is FrameworkElement { DataContext: RobloxInstanceInfo session }
        ? Instances.FirstOrDefault(current => current.Key == session.Key) : null;
    private RobloxInstanceInfo[] CanvasCloseSubjects(RobloxInstanceInfo? single, bool all) =>
        (all ? Instances : single is not null ? new[] { single } : InstancesGrid.SelectedItems.OfType<RobloxInstanceInfo>())
        .Select(item => Instances.FirstOrDefault(current => current.Key == item.Key))
        .OfType<RobloxInstanceInfo>().DistinctBy(item => item.Key).ToArray();
    private void CanvasCloseRow_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentCanvasSession(sender) is { } session) TerminateInstances(CanvasCloseSubjects(session, false));
        e.Handled = true;
    }
    private void CanvasCloseAll_Click(object sender, RoutedEventArgs e) => TerminateInstances(CanvasCloseSubjects(null, true));
    private void CanvasFocusRow_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentCanvasSession(sender) is { } session && !_layout.Focus(session))
            AppendLog(RobloxMultiManager.Core.L.F("A janela do PID {0} não pôde receber foco.",session.ProcessId));
        e.Handled = true;
    }
    private void CanvasDetails_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentCanvasSession(sender) is not { } session) return;
        var report = RobloxMultiManager.Core.L.F("Conta: {0}\nPID: {1}\n",session.ProfileText,session.ProcessId)
            + RobloxMultiManager.Core.L.F("Início local: {0:dd/MM/yyyy HH:mm:ss}\nInício UTC: {1:O}\n",session.StartTimeUtc.ToLocalTime(),session.StartTimeUtc)
            + RobloxMultiManager.Core.L.F("Tempo aberto: {0}\n",SessionTimeConverter.FormatElapsed(session.StartTimeUtc, DateTime.UtcNow))
            + RobloxMultiManager.Core.L.F("Estado: {0}\nJanela detectada: {1}\n",session.StatusText,(session.HasWindow ? RobloxMultiManager.Core.L.T("Sim") : RobloxMultiManager.Core.L.T("Não")))
            + RobloxMultiManager.Core.L.F("Cliente: {0}\nVersão solicitada: {1}\n",session.ClientText,session.RequestedVersionId ?? RobloxMultiManager.Core.L.T("Automática / externa"))
            + RobloxMultiManager.Core.L.F("Compatibilidade: {0}\nOrigem: {1}\n",session.VersionMatchText,session.OriginText)
            + RobloxMultiManager.Core.L.F("Usuário Roblox: {0}\nID da conta: {1}",session.AccountUsername ?? RobloxMultiManager.Core.L.T("Não identificado"),session.AccountUserId?.ToString() ?? RobloxMultiManager.Core.L.T("Não identificado"));
        OpenDrawer(RobloxMultiManager.Core.L.T("Detalhes da sessão"), RobloxMultiManager.Core.L.T("Identidade, versão e tempo de execução deste cliente."),
            new ReportPanel(RobloxMultiManager.Core.L.T("SESSÃO ROBLOX"), session.ProfileText, RobloxMultiManager.Core.L.T("Tempo contado desde a abertura do processo, no horário local do Windows."), report,
                [new(RobloxMultiManager.Core.L.T("PROCESSO"), session.ProcessId.ToString()), new(RobloxMultiManager.Core.L.T("ESTADO"), session.StatusText), new(RobloxMultiManager.Core.L.T("COMPATIBILIDADE"), session.VersionMatchText)]));
    }
    private void Canvas_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { InstancesGrid.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape) { InstancesGrid.UnselectAll(); e.Handled = true; }
        else if (e.Key == Key.Delete && InstancesGrid.SelectedItems.Count > 0) { CloseInstanceButton_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter && e.OriginalSource is ListBoxItem or InstanceMap && InstancesGrid.SelectedItem is not null)
        { FocusInstanceButton_Click(sender, e); e.Handled = true; }
    }
}
