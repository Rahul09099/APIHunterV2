namespace UnsecuredAPIKeys.Data.Common
{
    /// <summary>
    /// Search provider for finding API keys.
    /// </summary>
    public enum SearchProviderEnum
    {
        Unknown = -99,
        GitHub = 1
    }

    /// <summary>
    /// Status of an API key in the system.
    /// </summary>
    public enum ApiStatusEnum
    {
        /// <summary>The key was found but not yet checked for validity.</summary>
        Unverified = -99,

        /// <summary>The key was checked and is valid/working.</summary>
        Valid = 1,

        /// <summary>The key was checked and is not working (invalid, expired, revoked, etc.).</summary>
        Invalid = 0,

        /// <summary>The key is valid but has no credits/quota.</summary>
        ValidNoCredits = 7,

        /// <summary>The key was checked and is erroring out for some reason.</summary>
        Error = 6
    }

    /// <summary>
    /// Category of API provider.
    /// </summary>
    public enum ApiCategoryEnum
    {
        Unknown = -99,
        AIAndLLM = 1,
        Communication = 4,
        MapsAndLocation = 6
    }

    /// <summary>
    /// Type of API provider.
    /// </summary>
    public enum ApiTypeEnum
    {
        Unknown = -99,

        // AI & LLM Category (1)
        OpenAI = 100,
        AnthropicClaude = 120,
        GoogleAI = 130,
        Cohere = 140,
        HuggingFace = 150,
        StabilityAI = 160,
        Replicate = 180,
        TogetherAI = 190,
        DeepSeek = 198,
        ElevenLabs = 199,
        XAI = 207,
        FireworksAI = 208,
        KlingAI = 210,
        PolloAI = 215,
        RunwayML = 220,

        // Communication Category (4)
        SendGrid = 410,
        Mailgun = 425,
        Slack = 430,

        // Maps & Location Category (6)
        Mapbox = 600
    }
}
