using Microsoft.UI.Xaml;
using TcpLatencyMonitor.App.Services;

namespace TcpLatencyMonitor.App;

public sealed partial class MainWindow
{
    private void VerifyAnnotationSettings(Action<bool,string> check)
    {
        var fresh=Settings.Load(Path.Combine(AppPaths.DataDirectory,"never-created-settings.json"));
        check(fresh.EnableNodeMetadata&&fresh.BackgroundNodeMetadata&&!fresh.ResumeOnLaunch&&fresh.Address=="","new installation enables both annotation options without auto-starting a target");
        var legacy=Settings.ParseExisting("""{"Version":2,"Mode":"Both","Address":"127.0.0.1","TimeoutMilliseconds":700}""");
        check(!legacy.EnableNodeMetadata&&!legacy.BackgroundNodeMetadata&&legacy.Mode=="Both"&&legacy.TimeoutMilliseconds==700,"missing flags in old settings preserve offline mode and other user settings");
        var viewOnly=Settings.ParseExisting("""{"Version":2,"EnableNodeMetadata":true}""");
        check(viewOnly.EnableNodeMetadata&&!viewOnly.BackgroundNodeMetadata,"existing view-only preference is not expanded to background queries");
        var off=Settings.ParseExisting("""{"Version":2,"EnableNodeMetadata":false,"BackgroundNodeMetadata":true}""");
        check(!off.EnableNodeMetadata&&!off.BackgroundNodeMetadata,"disabled parent annotation switch overrides an inconsistent background flag");
        var automatic=Settings.ParseExisting("""{"Version":2,"EnableNodeMetadata":true,"BackgroundNodeMetadata":true}""");
        check(automatic.EnableNodeMetadata&&automatic.BackgroundNodeMetadata,"existing automatic annotation preference remains enabled");
        var broken=Path.Combine(AppPaths.DataDirectory,"broken-settings-fixture.json");File.WriteAllText(broken,"{broken");
        var recovered=Settings.Load(broken);
        check(!recovered.EnableNodeMetadata&&!recovered.BackgroundNodeMetadata&&!recovered.ResumeOnLaunch,"unreadable existing settings fail back to offline mode instead of new-install defaults");
        bool refused=false;try{recovered.Save(broken);}catch(IOException){refused=true;}
        check(refused&&File.ReadAllText(broken)=="{broken","an unreadable configuration is never overwritten by automatic saves");
        check(legacy.Profiles.Count==1&&legacy.Profiles[0].Mode=="Both"&&legacy.SelectedProfileId==legacy.Profiles[0].Id,"flat settings migrate into the first stable profile without changing its measurement configuration");
        var future=Path.Combine(AppPaths.DataDirectory,"future-settings-fixture.json");File.WriteAllText(future,"{\"Version\":99}");
        check(Settings.Load(future).LoadError is not null&&File.ReadAllText(future)=="{\"Version\":99}","newer settings versions remain intact and are rejected explicitly");
        MetadataBox.IsChecked=true;BackgroundMetadataBox.IsChecked=true;Metadata_Click(MetadataBox,new RoutedEventArgs());
        check(_annotationService!.Mode==2&&Settings.Load().BackgroundNodeMetadata&&AnnotationQueryNotice.Text.Contains("ipwho.is"),"automatic annotation settings persist and provider disclosure remains in annotation settings");
        MetadataBox.IsChecked=false;Metadata_Click(MetadataBox,new RoutedEventArgs());
        check(BackgroundMetadataBox.IsChecked!=true&&!BackgroundMetadataBox.IsEnabled&&_annotationService.Mode==0&&!Settings.Load().BackgroundNodeMetadata,"disabling online annotations clears and disables background queries");
    }
}
