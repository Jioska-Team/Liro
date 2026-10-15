using System.Text.Json;

namespace Liro.DevelopmentAgent.Configuration;

public sealed class AgentOptions
{
    public string BackendPath { get; set; } = "";
    public ulong ApplicationId { get; set; }
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong RoleId { get; set; }
    public ulong PanelMessageId { get; set; }
    public int? InterfaceIndex { get; set; }
    public string? DockerDesktopPath { get; set; }

    public static string LocalDirectory
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Liro", "DevelopmentAgent"); }
    }

    public static AgentOptions Load(string localDirectory)
    {
        var path = Path.Combine(localDirectory, "settings.json");
        var options = JsonSerializer.Deserialize<AgentOptions>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("La configuracion local no es valida.");
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (ApplicationId == 0 || GuildId == 0 || ChannelId == 0 || RoleId == 0
            || !Path.IsPathFullyQualified(BackendPath) || InterfaceIndex is <= 0
            || !File.Exists(Path.Combine(BackendPath, "src", "Liro.AppHost", "Liro.AppHost.csproj"))
            || !File.Exists(Path.Combine(BackendPath, "Start-Lan.ps1")))
        {
            throw new InvalidOperationException("Configura IDs validos y la ruta absoluta del backend local.");
        }
        if (DockerDesktopPath is not null && (!Path.IsPathFullyQualified(DockerDesktopPath) || !File.Exists(DockerDesktopPath)))
        {
            throw new InvalidOperationException("La ruta local de Docker Desktop no es valida.");
        }
    }

    public void Save(string localDirectory)
    {
        Validate();
        Directory.CreateDirectory(localDirectory);
        var path = Path.Combine(localDirectory, "settings.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
