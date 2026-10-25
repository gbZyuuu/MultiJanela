using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RobloxMultiManager;
using RobloxMultiManager.Core;
internal static partial class Program
{
    private sealed class CommunityUiHttp : HttpMessageHandler
    {
        public bool Owner;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var path=request.RequestUri!.AbsolutePath;
            object payload=path switch
            {
                "/v1/users/authenticated"=>new{id=42},
                "/v2/users/42/groups/roles"=>new{data=new[]{new{group=new{id=5,name="Aurora Creators",memberCount=1250},role=new{name="Member"}},new{group=new{id=6,name="Studio Community",memberCount=420},role=new{name="Member"}}}},
                "/v1/groups/5"=>new{id=5,name="Aurora Creators",description="A space for building, exploring and sharing experiences together.",shout=new{body="Welcome to the community."},memberCount=1250,owner=new{username="Aurora"}},
                "/v1/groups/5/membership"=>new{isOwner=Owner,canViewMemberList=true,userRole=new{role=new{rank=Owner?255:1}},permissions=new{groupMembershipPermissions=new{changeRank=Owner,removeMembers=Owner,inviteMembers=Owner},groupPostsPermissions=new{postToStatus=Owner}}},
                "/v1/groups/5/roles"=>new{roles=new[]{new{id=2,name="Member",rank=1,memberCount=1249},new{id=3,name="Moderator",rank=100,memberCount=1}}},
                "/v1/groups/5/users"=>new{data=new[]{new{user=new{userId=7,username="Explorer",displayName="Explorer"},role=new{id=2,name="Member",rank=1}}},nextPageCursor=(string?)null},
                _=>new{data=Array.Empty<object>()}
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(payload),Encoding.UTF8,"application/json")});
        }
    }
    private static void RunComfortChecks(string output, Window host)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var appearanceType=typeof(MainWindow).Assembly.GetType("RobloxMultiManager.AppearanceService")!;
        var originalContent=host.Content;var originalWidth=host.Width;var originalHeight=host.Height;
        var original=(AppearancePreferences)appearanceType.GetProperty("Current")!.GetValue(null)!;
        void Theme(string id)=>appearanceType.GetMethod("Apply")!.Invoke(null,[new AppearancePreferences(Theme:id)]);
        var panel=new AppearancePanel(new AppearancePreferencesStore(new AppPaths(Path.Combine(output,"comfort-theme-store"))));
        var frame=new Border{Padding=new Thickness(24),Child=panel};frame.SetResourceReference(Border.BackgroundProperty,"WindowBackground");
        UiMotion.Initialize();host.Content=frame;host.Width=760;host.Height=760;
        try
        {
            foreach(var id in new[]{"midnight","graphite","ocean","forest","amethyst","rose","sand","light","contrast"})
            {
                Theme(id);((ComboBox)panel.FindName("Theme")).SelectedValue=id;Render(frame,760,760,Path.Combine(output,"themes-"+id+".png"));
                Check(((SolidColorBrush)frame.Background).Color==((SolidColorBrush)Application.Current.Resources["WindowBackground"]).Color,"Existing window receives the new palette immediately: "+id);
                Check(((WrapPanel)panel.FindName("ThemeTiles")).Children.Count==9,"All nine theme previews remain available in "+id);
                var text=(SolidColorBrush)Application.Current.Resources["TextPrimary"];var surface=(SolidColorBrush)Application.Current.Resources["Surface"];
                Check(Math.Abs(text.Color.R-surface.Color.R)+Math.Abs(text.Color.G-surface.Color.G)+Math.Abs(text.Color.B-surface.Color.B)>350,"Readable primary text contrast in "+id);
            }
            var rose=((WrapPanel)panel.FindName("ThemeTiles")).Children.OfType<Button>().Single(b=>Equals(b.Tag,"rose"));
            rose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((AppearancePreferences)appearanceType.GetProperty("Current")!.GetValue(null)!).Theme=="rose","Theme click applies immediately without an Apply action");
            ((ComboBox)panel.FindName("Scale")).SelectedValue="125";
            ((ComboBox)panel.FindName("Font")).SelectedValue="Verdana";
            PumpUntil(()=>!panel.IsBusy);
            var read=new AppearancePreferencesStore(new AppPaths(Path.Combine(output,"comfort-theme-store"))).LoadAsync();PumpUntil(()=>read.IsCompleted);
            Check(read.Result.Theme=="rose"&&read.Result.TextScale==125&&read.Result.Font=="Verdana","Rapid appearance edits persist the newest combined selection");
            var account1=AccountProfile.Create("One");var account2=AccountProfile.Create("Two");var policy=GraphicsLaunchPolicy.Validate(new(true,60,4));
            var graphics=new GraphicsPanel(new RobloxGraphicsSettingsService(Path.Combine(output,"never-written.xml")),[account1,account2],policy,next=>{policy=next;return Task.CompletedTask;});
            host.Content=graphics;PumpUntil(()=>graphics.IsLoaded);
            ((ComboBox)graphics.FindName("Account")).SelectedIndex=1;((CheckBox)graphics.FindName("UseGlobal")).IsChecked=false;((ComboBox)graphics.FindName("Fps")).SelectedValue="120";
            ((ComboBox)graphics.FindName("Account")).SelectedIndex=2;((CheckBox)graphics.FindName("UseGlobal")).IsChecked=false;((Slider)graphics.FindName("Quality")).Value=2;
            PumpUntil(()=>!graphics.IsBusy);
            Check(policy.ForAccount(account1.Id).FpsCap==120&&policy.ForAccount(account2.Id).Quality==2,"Automatic graphics saves retain rapid edits across account scopes");
            var flagsBefore=UiPreferences.Current;UiPreferences.Apply(flagsBefore with{Animations=false});Check(!UiPreferences.Current.Animations,"Motion preference updates in memory immediately");UiPreferences.Apply(flagsBefore);
            var resolve=typeof(AvatarViewport).GetMethod("Resolve",BindingFlags.Static|BindingFlags.NonPublic)!;
            var origin=new Uri("https://t5.rbxcdn.com/manifest");
            Check(((Uri)resolve.Invoke(null,[origin,"180DAY-1a54662866d4e9a6db4d2105aa13579e"])!).Host=="t3.rbxcdn.com","3D assets use their own CDN shard including the lifetime prefix");
            Theme("midnight");
            var feedback=new Button { Content="Motion fixture", Style=new Style(typeof(Button)), Opacity=.5 };
            host.Content=feedback;Layout(feedback,160,48);PumpUntil(()=>feedback.IsLoaded);
            feedback.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=UIElement.MouseEnterEvent});
            Check(UiMotion.GetFeedback(feedback),"Global input feedback also reaches buttons with independent templates");
            typeof(UiMotion).GetMethod("Pulse",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[feedback,.7,30]);
            PumpUntil(()=>!feedback.HasAnimatedProperties);
            Check(feedback.Opacity==.5,"Feedback releases its clock and preserves the underlying opacity");
            Theme("midnight");
            using var handler=new CommunityUiHttp();using var http=new HttpClient(handler);using var communities=new CommunitiesPanel(http,42);
            communities.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));PumpUntil(()=>!communities.IsBusy);
            var detail=(TextBlock)communities.FindName("Title");Check(detail.Text=="Aurora Creators","Community details automatically load for the selected membership");
            Check(((TabItem)communities.FindName("ManageTab")).Visibility==Visibility.Collapsed,"Members do not see owner-only community controls");
            var communityFrame=new Border{Child=communities};communityFrame.SetResourceReference(Border.BackgroundProperty,"WindowBackground");host.Content=communityFrame;host.Width=900;host.Height=610;Render(communityFrame,900,610,Path.Combine(output,"communities-member.png"));
            handler.Owner=true;var refresh=communities.RefreshAsync();PumpUntil(()=>refresh.IsCompleted);refresh.GetAwaiter().GetResult();
            Check(((TabItem)communities.FindName("RequestsTab")).Visibility==Visibility.Visible&&((TabItem)communities.FindName("ManageTab")).Visibility==Visibility.Visible,"Authorized community management appears from fresh permissions");
            var tabs=(TabControl)communities.FindName("Tabs");tabs.SelectedIndex=1;PumpUntil(()=>!communities.IsBusy);
            Check(((ListBox)communities.FindName("Members")).Items.Count==1,"Member list loads on the first visit without an extra button");
            Render(communityFrame,900,610,Path.Combine(output,"communities-members.png"));
            tabs.SelectedItem=communities.FindName("ManageTab");Render(communityFrame,900,610,Path.Combine(output,"communities-manage.png"));
            var language=L.Language;L.Language=language=="en"?"pt-BR":"en";
            Check(((TextBlock)communities.FindName("Summary")).Text==L.F("{0:N0} membros · ID {1}",1250,5),"Community metadata follows instant language changes without reloading");L.Language=language;
            ((TextBox)communities.FindName("ShoutInput")).Text="Unsaved draft";Check(communities.HasDraft,"Unsaved community edits participate in the handoff guard");
            using var avatarHttp=new HttpClient(new SocialHttp());using var editor=new AvatarEditorPanel(new AccountSocialService(avatarHttp,42),"Aurora",null);
            editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));PumpUntil(()=>!editor.IsBusy);
            Check(((ComboBox)editor.FindName("RigType")).SelectedIndex==1,"Named R15 responses no longer silently disable the avatar preview");
            var colors=((System.Collections.IDictionary)typeof(AvatarEditorPanel).GetField("_colors",flags)!.GetValue(editor)!);
            Check(colors.Values.Cast<TextBox>().All(t=>t.Text=="#FFFFFF"),"Unprefixed API colors become valid editable hex colors");
        }
        finally { host.Content=originalContent;host.Width=originalWidth;host.Height=originalHeight;appearanceType.GetMethod("Apply")!.Invoke(null,[original]); }
    }
}
