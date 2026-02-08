namespace UnsecuredAPIKeys.Data.Common;

[AttributeUsage(AttributeTargets.Class)]
public class ApiProviderAttribute : Attribute
{
    /// <summary>
    /// Whether this provider should be used by the Scraper bot
    /// </summary>
    public bool ScraperUse { get; set; } = true;

    /// <summary>
    /// Whether this provider should be used by the Verifier bot
    /// </summary>
    public bool VerificationUse { get; set; } = true;

    /// <summary>
    /// Creates an ApiProvider attribute with default usage (enabled for both scraper and verifier)
    /// </summary>
    public ApiProviderAttribute()
    {
    }

    /// <summary>
    /// Creates an ApiProvider attribute with specific usage flags
    /// </summary>
    /// <param name="scraperUse">Enable for scraper bot</param>
    /// <param name="verificationUse">Enable for verifier bot</param>
    public ApiProviderAttribute(bool scraperUse, bool verificationUse)
    {
        ScraperUse = scraperUse;
        VerificationUse = verificationUse;
    }
}
