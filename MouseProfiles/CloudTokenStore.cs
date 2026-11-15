using System.Text.Json;

namespace MouseProfiles;

static class CloudTokenStore
{
    static string TokenPath => Path.Combine(
        Path.GetDirectoryName(ConfigStore.DefaultPath)!,
        "cloud-token.json");

    public static string? Load()
    {
        try
        {
            if (File.Exists(TokenPath))
            {
                var doc = JsonSerializer.Deserialize<TokenFile>(File.ReadAllText(TokenPath));
                return doc?.Token;
            }
        }
        catch { }
        return null;
    }

    public static void Save(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        File.WriteAllText(TokenPath, JsonSerializer.Serialize(new TokenFile(token)));
    }

    public static void Delete()
    {
        try { if (File.Exists(TokenPath)) File.Delete(TokenPath); } catch { }
    }

    record TokenFile(string Token);
}
