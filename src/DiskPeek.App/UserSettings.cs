using System.IO;
using System.Text.Json;

namespace DiskPeek.App;

internal sealed class UserSettings
{
    public string Theme { get; set; } = "System";
    public bool AutomaticUpdateChecks { get; set; } = true;
    public DateTime? LastUpdateCheckUtc { get; set; }
}

internal sealed class SettingsStore
{
    public string FilePath { get; }
    public UserSettings Settings { get; private set; } = new();
    public string? LoadError { get; private set; }
    public SettingsStore(string? filePath = null)
    {
        // Roaming preferences survive replacement/removal of the local installer root.
        FilePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskPeek", "settings.json");
        try
        {
            if (!File.Exists(FilePath)) return;
            if (new FileInfo(FilePath).Length > 65536) throw new InvalidDataException("Settings file is too large.");
            Settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new();
            if (Settings.Theme is not ("System" or "Dark" or "Light")) Settings.Theme = "System";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        {
            LoadError = ex.Message;
            Settings = new();
        }
    }

    public bool TrySave(out string? error)
    {
        error = null;
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }
}
