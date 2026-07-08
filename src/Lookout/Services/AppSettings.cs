using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lookout.Services;

/// <summary>
/// Non-secret app settings (provider choice, model IDs, base URL), persisted as
/// JSON in %USERPROFILE%\.lookout\settings.json. API keys live in
/// <see cref="SecureStore"/>, never here.
/// </summary>
public sealed class AppSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProviderKind Provider { get; set; } = ProviderKind.Anthropic;

    public string AnthropicModel { get; set; } = ClaudeApiService.DefaultModel;

    public string GoogleModel { get; set; } = OpenAiCompatibleProvider.GoogleDefaultModel;

    public string OpenRouterModel { get; set; } = OpenAiCompatibleProvider.DefaultModel;

    public string OpenRouterBaseUrl { get; set; } = OpenAiCompatibleProvider.DefaultBaseUrl;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The credential account name for the active provider's key.</summary>
    [JsonIgnore]
    public string ActiveKeyAccount => Provider switch
    {
        ProviderKind.OpenRouter => SecureStore.OpenRouterAccount,
        ProviderKind.Google => SecureStore.GoogleAccount,
        _ => SecureStore.AnthropicAccount,
    };

    /// <summary>Loads settings, returning defaults if the file is missing or invalid.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(LookoutPaths.SettingsFile))
                return new AppSettings();
            var json = File.ReadAllText(LookoutPaths.SettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>Persists settings to disk. Non-fatal on failure.</summary>
    public void Save()
    {
        try
        {
            LookoutPaths.EnsureRoot();
            File.WriteAllText(LookoutPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // Settings are best-effort; a failed write shouldn't crash the app.
        }
    }
}
