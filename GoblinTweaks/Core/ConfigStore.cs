using System.Text.Json;
using System.Text.Json.Nodes;

namespace GoblinTweaks.Core;

public sealed class PluginConfig
{
    public int Version { get; set; } = 1;
    public HashSet<string> EnabledTweaks { get; set; } = [];
    public Dictionary<string, JsonObject> TweakSettings { get; set; } = [];
    public bool ShowWelcome { get; set; } = true;

    /// <summary>Plugin language ("en" or "es"). Independent from the Dalamud language.</summary>
    public string Language { get; set; } = "en";
}

/// <summary>
/// Loads and saves the plugin configuration as plain JSON (pluginConfigs/GoblinTweaks.json).
/// Writes are atomic, and a damaged file is backed up instead of crashing the plugin.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true,
    };

    private readonly FileInfo _file = Svc.PluginInterface.ConfigFile;

    public PluginConfig Data { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (!_file.Exists || _file.Length == 0)
                return;

            Data = JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(_file.FullName), JsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            var backup = _file.FullName + $".broken-{DateTime.Now:yyyyMMddHHmmss}";
            Svc.Log.Error(ex, "Config could not be read, starting with defaults. Backup: {path}", backup);
            try { _file.CopyTo(backup, true); } catch { /* best effort */ }
            Data = new();
        }
    }

    public void Save()
    {
        try
        {
            _file.Directory?.Create();
            var temp = _file.FullName + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Data, JsonOptions));
            File.Move(temp, _file.FullName, true);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Could not save config");
        }
    }

    public T GetSettings<T>(string tweakId) where T : class, new()
    {
        if (!Data.TweakSettings.TryGetValue(tweakId, out var node))
            return new T();

        try
        {
            // Unknown/missing fields are ignored, so adding options never needs a migration.
            return node.Deserialize<T>(JsonOptions) ?? new T();
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "Settings of {tweak} could not be read, using defaults", tweakId);
            return new T();
        }
    }

    public void SetSettings<T>(string tweakId, T settings) where T : class
    {
        if (JsonSerializer.SerializeToNode(settings, JsonOptions) is JsonObject obj)
            Data.TweakSettings[tweakId] = obj;

        Save();
    }
}
