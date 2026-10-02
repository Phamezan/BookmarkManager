using BookmarkManager.Api.Services;
using BookmarkManager.Contracts;

namespace BookmarkManager.UnitTests;

public sealed class AiTaggingSettingsMaskingTests
{
    [Fact]
    public void ToMasked_ReplacesEverySecretAndSetsHasFlags()
    {
        var masked = AiTaggingSettingsMasking.ToMasked(new AiTaggingSettingsDto
        {
            ApiKey = "abcdef1234",
            GroqApiKey = "groqkey5678",
            GeminiApiKey = "geminkey9012",
            RagApiKey = "ragkey3456",
            RagFallbackApiKey = "fallback7890"
        });

        Assert.Equal("••••1234", masked.ApiKey);
        Assert.Equal("••••5678", masked.GroqApiKey);
        Assert.Equal("••••9012", masked.GeminiApiKey);
        Assert.Equal("••••3456", masked.RagApiKey);
        Assert.Equal("••••7890", masked.RagFallbackApiKey);
        Assert.True(masked.HasApiKey);
        Assert.True(masked.HasGroqApiKey);
        Assert.True(masked.HasGeminiApiKey);
        Assert.True(masked.HasRagApiKey);
        Assert.True(masked.HasRagFallbackApiKey);
    }

    [Fact]
    public void ToMasked_EmptySecretsStayEmptyAndHasFlagsFalse()
    {
        var masked = AiTaggingSettingsMasking.ToMasked(new AiTaggingSettingsDto());

        Assert.Equal(string.Empty, masked.ApiKey);
        Assert.False(masked.HasApiKey);
        Assert.False(masked.HasRagFallbackApiKey);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("abcdefgh")]
    public void Mask_ShortSecret_RevealsNoPlaintext(string secret)
        => Assert.Equal(AiTaggingSettingsMasking.MaskPrefix, AiTaggingSettingsMasking.Mask(secret));

    [Fact]
    public void Mask_NineCharacterSecret_RevealsOnlyLastFour()
        => Assert.Equal("••••fghi", AiTaggingSettingsMasking.Mask("abcdefghi"));

    [Fact]
    public void MergeSecrets_EmptyOrMasked_KeepsStored_NewReplaces_ClearClears()
    {
        var stored = new AiTaggingSettingsDto
        {
            ApiKey = "stored-openrouter",
            GroqApiKey = "stored-groq",
            GeminiApiKey = "stored-gemini"
        };

        var merged = AiTaggingSettingsMasking.MergeSecrets(new AiTaggingSettingsDto
        {
            ApiKey = "••••uter",
            GroqApiKey = string.Empty,
            GeminiApiKey = "brand-new-gemini",
            ClearApiKey = false
        }, stored);

        Assert.Equal("stored-openrouter", merged.ApiKey);
        Assert.Equal("stored-groq", merged.GroqApiKey);
        Assert.Equal("brand-new-gemini", merged.GeminiApiKey);
        Assert.Equal("stored-gemini", stored.GeminiApiKey);
    }

    [Fact]
    public void MergeSecrets_ClearFlagWinsOverTypedValue()
    {
        var stored = new AiTaggingSettingsDto { ApiKey = "stored-openrouter" };

        var merged = AiTaggingSettingsMasking.MergeSecrets(new AiTaggingSettingsDto
        {
            ApiKey = "typed-but-cleared",
            ClearApiKey = true
        }, stored);

        Assert.Equal(string.Empty, merged.ApiKey);
    }

    [Fact]
    public void MergeTestSecret_ResolvesStoredSecretByNameOrProvider()
    {
        var stored = new AiTaggingSettingsDto
        {
            ApiKey = "stored-openrouter",
            GroqApiKey = "stored-groq",
            RagApiKey = "stored-rag"
        };

        var empty = AiTaggingSettingsMasking.MergeTestSecret(new TestAiKeyRequest { SecretName = "RagApiKey" }, stored);
        Assert.Equal("stored-rag", empty.ApiKey);

        var legacyGroq = AiTaggingSettingsMasking.MergeTestSecret(new TestAiKeyRequest { Provider = "Groq" }, stored);
        Assert.Equal("stored-groq", legacyGroq.ApiKey);

        var typed = AiTaggingSettingsMasking.MergeTestSecret(new TestAiKeyRequest
        {
            SecretName = "RagApiKey",
            ApiKey = "typed-rag"
        }, stored);
        Assert.Equal("typed-rag", typed.ApiKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("••••7890")]
    public void MergeTestSecret_WhenStoredKeyUsed_PinsStoredBaseUrlAndIgnoresCallerBaseUrl(string incoming)
    {
        var stored = new AiTaggingSettingsDto
        {
            GroqApiKey = "stored-groq-7890",
            GroqBaseUrl = "https://stored.groq.example/v1"
        };

        var merged = AiTaggingSettingsMasking.MergeTestSecret(new TestAiKeyRequest
        {
            Provider = "Groq",
            SecretName = "GroqApiKey",
            BaseUrl = "https://attacker.example/v1",
            ApiKey = incoming
        }, stored);

        Assert.Equal("stored-groq-7890", merged.ApiKey);
        Assert.Equal("https://stored.groq.example/v1", merged.BaseUrl);
        Assert.Equal("Groq", merged.Provider);
    }

    [Fact]
    public void MergeTestSecret_StoredUrlBlank_UsesBuiltInDefaultForSecret()
    {
        var stored = new AiTaggingSettingsDto { RagFallbackApiKey = "stored-fallback" };

        var merged = AiTaggingSettingsMasking.MergeTestSecret(new TestAiKeyRequest
        {
            SecretName = "RagFallbackApiKey",
            BaseUrl = "https://attacker.example/v1",
            ApiKey = string.Empty
        }, stored);

        Assert.Equal("https://integrate.api.nvidia.com/v1", merged.BaseUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("••••7890")]
    public void FindEndpointChangeViolation_PreservedKeyAndChangedUrl_ReturnsMessage(string incoming)
    {
        var stored = new AiTaggingSettingsDto
        {
            GroqApiKey = "stored-groq-7890",
            GroqBaseUrl = "https://api.groq.com/openai/v1"
        };

        var violation = AiTaggingSettingsMasking.FindEndpointChangeViolation(new AiTaggingSettingsDto
        {
            GroqApiKey = incoming,
            GroqBaseUrl = "https://attacker.example/v1"
        }, stored);

        Assert.NotNull(violation);
        Assert.Contains("Re-enter the Groq API key when changing its endpoint", violation);
    }

    [Fact]
    public void FindEndpointChangeViolation_TrailingSlashAndHostCaseAreInsignificant()
    {
        var stored = new AiTaggingSettingsDto
        {
            GroqApiKey = "stored-groq",
            GroqBaseUrl = "https://api.groq.com/openai/v1"
        };

        var violation = AiTaggingSettingsMasking.FindEndpointChangeViolation(new AiTaggingSettingsDto
        {
            GroqApiKey = string.Empty,
            GroqBaseUrl = "https://API.GROQ.COM/openai/v1/"
        }, stored);

        Assert.Null(violation);
    }

    [Fact]
    public void FindEndpointChangeViolation_TypedKeyClearOrNoStoredKey_AllowsChangedUrl()
    {
        var stored = new AiTaggingSettingsDto
        {
            GroqApiKey = "stored-groq",
            GroqBaseUrl = "https://api.groq.com/openai/v1"
        };

        Assert.Null(AiTaggingSettingsMasking.FindEndpointChangeViolation(new AiTaggingSettingsDto
        {
            GroqApiKey = "typed-new",
            GroqBaseUrl = "https://attacker.example/v1"
        }, stored));

        Assert.Null(AiTaggingSettingsMasking.FindEndpointChangeViolation(new AiTaggingSettingsDto
        {
            ClearGroqApiKey = true,
            GroqBaseUrl = "https://attacker.example/v1"
        }, stored));

        Assert.Null(AiTaggingSettingsMasking.FindEndpointChangeViolation(new AiTaggingSettingsDto
        {
            GroqApiKey = string.Empty,
            GroqBaseUrl = "https://attacker.example/v1"
        }, new AiTaggingSettingsDto()));
    }

    [Fact]
    public void MergeTestSecret_TypedKey_KeepsCallerBaseUrlAndProvider()
    {
        var stored = new AiTaggingSettingsDto { GroqApiKey = "stored-groq" };

        var merged = AiTaggingSettingsMasking.MergeTestSecret(new TestAiKeyRequest
        {
            Provider = "Groq",
            SecretName = "GroqApiKey",
            BaseUrl = "https://typed.example/v1",
            ApiKey = "typed-groq"
        }, stored);

        Assert.Equal("typed-groq", merged.ApiKey);
        Assert.Equal("https://typed.example/v1", merged.BaseUrl);
    }

    [Fact]
    public void MergeTestSecret_GeminiSecret_PinsEndpointAndGeminiProvider()
    {
        var stored = new AiTaggingSettingsDto
        {
            GeminiApiKey = "gemini-secret-key",
            Endpoint = "https://stored.gemini.example/v1beta"
        };

        var merged = AiTaggingSettingsMasking.MergeTestSecret(new TestAiKeyRequest
        {
            Provider = "OpenRouter",
            SecretName = "GeminiApiKey",
            BaseUrl = "https://attacker.example/v1",
            ApiKey = string.Empty
        }, stored);

        Assert.Equal("gemini-secret-key", merged.ApiKey);
        Assert.Equal("https://stored.gemini.example/v1beta", merged.BaseUrl);
        Assert.Equal("Gemini", merged.Provider);
    }

    [Fact]
    public void FindMaskInjectionViolation_CrossSecretMask_Rejected()
    {
        var stored = new AiTaggingSettingsDto
        {
            ApiKey = "openrouter-secret-1111",
            GroqApiKey = "groq-secret-2222"
        };

        // ApiKey's mask pasted into the Groq field.
        var violation = AiTaggingSettingsMasking.FindMaskInjectionViolation(new AiTaggingSettingsDto
        {
            GroqApiKey = "••••1111"
        }, stored);

        Assert.NotNull(violation);
        Assert.Contains("Groq", violation);
    }

    [Fact]
    public void FindMaskInjectionViolation_OwnMask_Allowed()
    {
        var stored = new AiTaggingSettingsDto { GroqApiKey = "groq-secret-2222" };

        Assert.Null(AiTaggingSettingsMasking.FindMaskInjectionViolation(new AiTaggingSettingsDto
        {
            GroqApiKey = "••••2222"
        }, stored));

        Assert.Null(AiTaggingSettingsMasking.FindMaskInjectionViolation(new AiTaggingSettingsDto
        {
            GroqApiKey = "gsk-real-typed"
        }, stored));
    }

    [Fact]
    public void MergeSecrets_ForeignMask_KeepsStoredNeverStoresLiteral()
    {
        var stored = new AiTaggingSettingsDto { GroqApiKey = "groq-secret-2222" };

        var merged = AiTaggingSettingsMasking.MergeSecrets(new AiTaggingSettingsDto
        {
            GroqApiKey = "••••1111"
        }, stored);

        Assert.Equal("groq-secret-2222", merged.GroqApiKey);
    }

    [Fact]
    public void FindEndpointChangeViolation_EndpointChangedWhileApiKeyPreserved_ReturnsMessage()
    {
        var stored = new AiTaggingSettingsDto
        {
            ApiKey = "openrouter-secret-key",
            Endpoint = "https://generativelanguage.googleapis.com/v1beta"
        };

        var violation = AiTaggingSettingsMasking.FindEndpointChangeViolation(new AiTaggingSettingsDto
        {
            ApiKey = string.Empty,
            Endpoint = "https://attacker.example/v1beta"
        }, stored);

        Assert.NotNull(violation);
        Assert.Contains("when changing its endpoint", violation);
    }

    [Fact]
    public void FindEndpointChangeViolation_EndpointChangedWhileGeminiKeyPreserved_ReturnsMessage()
    {
        var stored = new AiTaggingSettingsDto
        {
            GeminiApiKey = "gemini-secret-key",
            Endpoint = "https://generativelanguage.googleapis.com/v1beta"
        };

        var violation = AiTaggingSettingsMasking.FindEndpointChangeViolation(new AiTaggingSettingsDto
        {
            GeminiApiKey = string.Empty,
            Endpoint = "https://attacker.example/v1beta"
        }, stored);

        Assert.NotNull(violation);
        Assert.Contains("Gemini", violation);
    }
}
