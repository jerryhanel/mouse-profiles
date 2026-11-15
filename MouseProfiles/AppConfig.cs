using System.Text.Json;

namespace MouseProfiles;

public sealed class OverlayConfig
{
    public int DurationMs { get; set; } = 1500;
    /// <summary>0.1 to 1.0</summary>
    public double Opacity { get; set; } = 0.75;
    /// <summary>Center, Top or Bottom</summary>
    public string Position { get; set; } = "Center";
}

public sealed class Profile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "New preset";
    /// <summary>What each mouse button emits, one entry per button (e.g. "Ctrl+V"); null = nothing.</summary>
    public List<string?> Outputs { get; set; } = new();
    /// <summary>Per-button flag: show a trigger preview toast when this button fires.</summary>
    public List<bool> Preview { get; set; } = new();
}

public sealed class AppConfig
{
    public int ButtonCount { get; set; } = 2;
    /// <summary>The captured signal for each programmable mouse button (MK1, MK2, ...); null = not captured yet.</summary>
    public List<string?> Buttons { get; set; } = new();
    public string? ScrollUp { get; set; }
    public string? ScrollDown { get; set; }
    public bool WrapAround { get; set; } = true;
    public bool RememberLast { get; set; } = true;
    public int LastProfile { get; set; }
    /// <summary>Send scan codes (what games expect) instead of virtual-key codes.</summary>
    public bool UseScanCodes { get; set; } = true;
    public bool RunAtStartup { get; set; } = true;
    public OverlayConfig Overlay { get; set; } = new();
    public List<Profile> Profiles { get; set; } = new();

    public static AppConfig CreateDefault() => new()
    {
        Profiles =
        {
            new Profile { Name = "Minecraft", Outputs = { "Shift+1", "Shift+7" } },
            new Profile { Name = "Word Stuff", Outputs = { "Ctrl+C", "Ctrl+V" } },
        },
    };

    /// <summary>Makes the button and output lists match ButtonCount so the UI and engine can index freely.</summary>
    public void Normalize()
    {
        ButtonCount = Math.Clamp(ButtonCount, 1, 16);
        Resize(Buttons, ButtonCount);
        foreach (var p in Profiles)
        {
            if (p.Id == Guid.Empty) p.Id = Guid.NewGuid();
            Resize(p.Outputs, ButtonCount);
            Resize(p.Preview, ButtonCount);
        }
        Overlay.Opacity = Math.Clamp(Overlay.Opacity, 0.1, 1.0);
        Overlay.DurationMs = Math.Clamp(Overlay.DurationMs, 200, 10000);
        LastProfile = Profiles.Count == 0 ? 0 : Math.Clamp(LastProfile, 0, Profiles.Count - 1);
    }

    static void Resize(List<string?> list, int n)
    {
        while (list.Count < n) list.Add(null);
        if (list.Count > n) list.RemoveRange(n, list.Count - n);
    }

    static void Resize(List<bool> list, int n)
    {
        while (list.Count < n) list.Add(false);
        if (list.Count > n) list.RemoveRange(n, list.Count - n);
    }

    public bool HasAnySignal => Buttons.Any(b => b != null) || ScrollUp != null || ScrollDown != null;

    public AppConfig Clone()
    {
        var c = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this)) ?? new AppConfig();
        c.Normalize();
        return c;
    }
}

public static class ConfigStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>MOUSEPROFILES_CONFIG overrides the location (used by automated tests so they never touch the real config).</summary>
    public static string DefaultPath => Environment.GetEnvironmentVariable("MOUSEPROFILES_CONFIG") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MouseProfiles", "config.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json);
                if (cfg != null) { cfg.Normalize(); return cfg; }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // fall through to defaults; the broken file is left alone until the next save
        }
        var def = AppConfig.CreateDefault();
        def.Normalize();
        return def;
    }

    /// <summary>
    /// Like Load, but returns false when falling back to defaults (file missing or parse failure).
    /// Use when callers need to distinguish a parsed config from generated defaults.
    /// </summary>
    public static bool TryLoad(out AppConfig config, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json);
                if (cfg != null) { cfg.Normalize(); config = cfg; return true; }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        config = AppConfig.CreateDefault();
        config.Normalize();
        return false;
    }

    public static void Save(AppConfig cfg, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, Json));
    }
}
