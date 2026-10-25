using System.Windows;

namespace RobloxMultiManager;

public sealed record ReportMetric(string Label, string Value);
public partial class ReportPanel : System.Windows.Controls.UserControl
{
    public string Eyebrow { get; }
    public string Title { get; }
    public string Summary { get; }
    public string Report { get; }
    public IReadOnlyList<ReportMetric> Metrics { get; }
    public ReportPanel(string eyebrow, string title, string summary, string report, IReadOnlyList<ReportMetric> metrics)
    {
        Eyebrow = eyebrow; Title = title; Summary = summary; Report = report; Metrics = metrics;
        InitializeComponent(); DataContext = this;
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(Report); CopyStatus.Text = RobloxMultiManager.Core.L.T("Relatório copiado."); }
        catch (System.Runtime.InteropServices.COMException) { CopyStatus.Text = RobloxMultiManager.Core.L.T("Área de transferência ocupada. Tente novamente."); }
    }
}
