namespace Lookout.Services;

/// <summary>Builds the active <see cref="IChatProvider"/> from settings + stored key.</summary>
public static class ChatProviderFactory
{
    public static IChatProvider Create(AppSettings settings)
    {
        var apiKey = SecureStore.Load(settings.ActiveKeyAccount) ?? string.Empty;

        return settings.Provider switch
        {
            ProviderKind.Google => new OpenAiCompatibleProvider(
                apiKey, settings.GoogleModel, OpenAiCompatibleProvider.GoogleBaseUrl),
            ProviderKind.OpenRouter => new OpenAiCompatibleProvider(
                apiKey, settings.OpenRouterModel, settings.OpenRouterBaseUrl),
            _ => new ClaudeApiService(apiKey, settings.AnthropicModel),
        };
    }

    /// <summary>Whether the active provider has an API key stored.</summary>
    public static bool HasActiveKey(AppSettings settings) => SecureStore.Has(settings.ActiveKeyAccount);

    /// <summary>Prompt shown in the API-key bar for the active provider.</summary>
    public static string KeyPrompt(AppSettings settings) => settings.Provider switch
    {
        ProviderKind.Google => "Enter your Google Gemini API key to start chatting.",
        ProviderKind.OpenRouter => "Enter your OpenRouter API key to start chatting.",
        _ => "Enter your Anthropic API key to start chatting.",
    };

    /// <summary>Placeholder text for the key input for the active provider.</summary>
    public static string KeyPlaceholder(AppSettings settings) => settings.Provider switch
    {
        ProviderKind.Google => "AIza… or AQ.…",
        ProviderKind.OpenRouter => "sk-or-...",
        _ => "sk-ant-...",
    };
}
