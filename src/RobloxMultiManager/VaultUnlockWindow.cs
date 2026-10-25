using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RobloxMultiManager.Core;
using Button=System.Windows.Controls.Button;
namespace RobloxMultiManager;
internal sealed class VaultUnlockWindow:Window
{
    public VaultUnlockWindow(AccountVault vault)
    {
        Title=L.T("Desbloquear cofre");Width=460;Height=340;MinWidth=420;WindowStartupLocation=WindowStartupLocation.CenterScreen;ResizeMode=ResizeMode.NoResize;SetResourceReference(BackgroundProperty,"WindowBackground");SetResourceReference(ForegroundProperty,"TextPrimary");
        var form=new StackPanel{Margin=new Thickness(28)};var title=new TextBlock{Text=L.T("Seu espaço está protegido"),FontSize=24,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap};form.Children.Add(title);
        form.Children.Add(new TextBlock{Text=L.T("Digite a senha do cofre para acessar suas contas."),Margin=new Thickness(0,10,0,20),TextWrapping=TextWrapping.Wrap});
        var password=new PasswordBox{FontSize=18,Padding=new Thickness(10),Margin=new Thickness(0,0,0,16)};password.SetResourceReference(BackgroundProperty,"SurfaceInset");password.SetResourceReference(ForegroundProperty,"TextPrimary");form.Children.Add(password);
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};status.SetResourceReference(TextBlock.ForegroundProperty,"DangerText");
        var button=new Button{Content=L.T("Desbloquear"),IsDefault=true,MinHeight=40};form.Children.Add(button);form.Children.Add(status);Content=form;
        button.Click+=async(_,_)=>{button.IsEnabled=false;var secret=password.Password;try{await Task.Run(()=>vault.UnlockAsync(secret));password.Clear();DialogResult=true;}catch(Exception){status.Text=L.T("Senha incorreta ou dados do cofre indisponíveis.");await Task.Delay(1200);}finally{button.IsEnabled=true;}};
        Loaded+=(_,_)=>password.Focus();
    }
    public static async Task<bool> UnlockIfNeededAsync(AppPaths paths,Window? owner=null)
    {
        var vault=new AccountVault(paths.Root);if(!vault.Exists)return true;await vault.ReadAsync();var prompt=new VaultUnlockWindow(vault);if(owner is not null){prompt.Owner=owner;prompt.WindowStartupLocation=WindowStartupLocation.CenterOwner;}return prompt.ShowDialog()==true;
    }
}
