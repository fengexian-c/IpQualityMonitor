using System.Text.Json;
using TcpLatencyMonitor.Core;
using System.Text.Json.Serialization;

namespace TcpLatencyMonitor.App.Services;

public sealed class Settings : TargetProfile
{
    public int Version {get;set;}=4;
    public GlobalMonitorSettings GlobalMonitoring {get;set;}=new();
    public bool EnableNodeMetadata {get;set;}
    public bool BackgroundNodeMetadata {get;set;}
    public int TimelineDays {get;set;}=1;
    public string GeoPrimary {get;set;}="ipwho.is";
    public bool GeoCrossCheck {get;set;}
    public int GeoRefreshHours {get;set;}=24;
    public string SelectedProfileId {get;set;}="";
    public List<TargetProfile> Profiles {get;set;}=new();
    public List<TargetProfile> ArchivedProfiles {get;set;}=new();
    [JsonIgnore] public string? LoadError {get;private set;}
    public static Settings NewInstallation()=>new(){EnableNodeMetadata=true,BackgroundNodeMetadata=true};
    public static Settings ParseExisting(string text)
    {
        using var document=JsonDocument.Parse(text);
        if(document.RootElement.ValueKind!=JsonValueKind.Object)throw new JsonException("Settings must be an object");
        var settings=JsonSerializer.Deserialize(text,SettingsJson.Default.Settings)??throw new JsonException("Empty settings");
        // Missing flags in existing files retain the historical offline/view-only behavior.
        if(!document.RootElement.TryGetProperty("Version",out _)){settings.Mode="Tcp";settings.Version=2;}
        if(settings.Mode is not ("Icmp" or "Tcp" or "Both"))settings.Mode="Icmp";
        if(settings.Version>4)throw new JsonException("配置由更新版本创建，请使用对应版本打开。");
        if(!settings.EnableNodeMetadata)settings.BackgroundNodeMetadata=false;
        if(!document.RootElement.TryGetProperty("Profiles",out _)&&!string.IsNullOrWhiteSpace(settings.Address))
        {
            var first=settings.Copy();first.Validate();settings.Profiles.Add(first);settings.SelectedProfileId=first.Id;
        }
        if(settings.Profiles is null||settings.ArchivedProfiles is null)throw new JsonException("目标列表无效。");
        var ids=new HashSet<string>();
        foreach(var profile in settings.Profiles){profile.Validate();if(!ids.Add(profile.Id))throw new JsonException("目标 ID 重复。");}
        if(!settings.Profiles.Any(p=>p.Id==settings.SelectedProfileId))settings.SelectedProfileId=settings.Profiles.FirstOrDefault()?.Id??"";
        if(!document.RootElement.TryGetProperty("GlobalMonitoring",out _))
            settings.GlobalMonitoring=GlobalMonitorSettings.From(settings.Profiles.FirstOrDefault(p=>p.Id==settings.SelectedProfileId)??settings);
        if(settings.GlobalMonitoring is null)throw new JsonException("统一设置无效。");
        settings.ApplyGlobals();
        settings.TimelineDays=settings.TimelineDays==7?7:1;settings.Version=4;
        if(settings.GeoPrimary is not ("ipwho.is" or "ipinfo-core" or "nexttrace"))settings.GeoPrimary="ipwho.is";
        if(settings.GeoRefreshHours is <1 or >168)settings.GeoRefreshHours=24;
        return settings;
    }
    public static Settings Load(string? path=null)
    {
        path??=AppPaths.SettingsPath;
        try
        {
            return ParseExisting(File.ReadAllText(path));
        }
        catch(FileNotFoundException){return NewInstallation();}
        catch(DirectoryNotFoundException){return NewInstallation();}
        catch(Exception ex) { StartupDiagnostics.Write("Settings could not be read; original file retained.",ex); return new(){LoadError=ex.Message}; }
    }
    public void Save(string? path=null)
    {
        if(LoadError is not null)throw new IOException("原配置未能读取，已保留原文件："+LoadError);
        ApplyGlobals();Version=4;
        path??=AppPaths.SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp=path+".tmp";
        // Keep the original file before the first successful v3 configuration write.
        if(File.Exists(path)&&!File.Exists(path+".before-multi.bak"))File.Copy(path,path+".before-multi.bak");
        if(File.Exists(path)&&!File.Exists(path+".before-global.bak"))File.Copy(path,path+".before-global.bak");
        using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None))
        {JsonSerializer.Serialize(stream,this,SettingsJson.Default.Settings);stream.Flush(true);}
        File.Move(temp,path,true);
    }
    public void ApplyGlobals()
    {
        GlobalMonitoring.Validate();
        foreach(var p in Profiles)GlobalMonitoring.Apply(p);
        // Archived profiles retain their original policy until explicitly restored.
        GlobalMonitoring.Apply(this,false);
    }
    public Settings WithGlobalMonitoring(GlobalMonitorSettings policy)
    {
        var copy=ParseExisting(JsonSerializer.Serialize(this,SettingsJson.Default.Settings));
        copy.GlobalMonitoring=policy;copy.ApplyGlobals();return copy;
    }
}
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(TargetProfile))]
[JsonSourceGenerationOptions(WriteIndented=true)]
internal partial class SettingsJson : JsonSerializerContext { }
