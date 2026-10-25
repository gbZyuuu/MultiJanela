using System.Windows;
using RobloxMultiManager.Core;
namespace RobloxMultiManager;
public partial class MainWindow
{
    private void FeaturesHelp_Click(object sender, RoutedEventArgs e) => OpenDrawer(L.T("Guia das ferramentas"), L.T("O que cada função faz"), new FeatureGuidePanel());
}
