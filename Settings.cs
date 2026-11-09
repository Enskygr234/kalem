using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kalem;

/// <summary>Settings in %AppData%\Kalem. The API key is encrypted with Windows DPAPI (current user only).</summary>
public sealed class Settings
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kalem");
    static readonly string FilePath = Path.Combine(Dir, "ayarlar.json");

    public string? ProtectedKey { get; set; }
    public string Model { get; set; } = Gemini.DefaultModel;

    public static Settings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Saved key, falling back to the GEMINI_API_KEY / GOOGLE_API_KEY environment variables.
    /// Never serialized: only the DPAPI-protected form is written to disk.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ApiKey
    {
        get
        {
            if (ProtectedKey != null)
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedKey), null, DataProtectionScope.CurrentUser)); }
                catch { }
            return Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool KeyFromEnvironment => ProtectedKey == null && ApiKey != null;

    public void SetApiKey(string? key)
    {
        ProtectedKey = string.IsNullOrWhiteSpace(key) ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
    }
}
