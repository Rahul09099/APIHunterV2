using System.Reflection;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Providers
{
    public static class ApiProviderRegistry
    {
        private static readonly Lazy<List<IApiKeyProvider>> _allProviders = new(() =>
        {
            return [.. Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(type => type.GetCustomAttribute<ApiProviderAttribute>() != null
                               && typeof(IApiKeyProvider).IsAssignableFrom(type)
                               && !type.IsInterface
                               && !type.IsAbstract)
                .Select(type => (IApiKeyProvider)Activator.CreateInstance(type)!)];
        });

        private static readonly Lazy<List<IApiKeyProvider>> _scraperProviders = new(() =>
        {
            return [.. Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(type => {
                    var attr = type.GetCustomAttribute<ApiProviderAttribute>();
                    return attr != null
                           && attr.ScraperUse
                           && typeof(IApiKeyProvider).IsAssignableFrom(type)
                           && !type.IsInterface
                           && !type.IsAbstract;
                })
                .Select(type => (IApiKeyProvider)Activator.CreateInstance(type)!)];
        });

        private static readonly Lazy<List<IApiKeyProvider>> _verifierProviders = new(() =>
        {
            return [.. Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(type => {
                    var attr = type.GetCustomAttribute<ApiProviderAttribute>();
                    return attr != null
                           && attr.VerificationUse
                           && typeof(IApiKeyProvider).IsAssignableFrom(type)
                           && !type.IsInterface
                           && !type.IsAbstract;
                })
                .Select(type => (IApiKeyProvider)Activator.CreateInstance(type)!)];
        });

        /// <summary>
        /// Gets all providers with ApiProvider attribute (backward compatibility)
        /// </summary>
        public static IReadOnlyList<IApiKeyProvider> Providers => _allProviders.Value;

        /// <summary>
        /// Gets providers that are enabled for scraper use
        /// </summary>
        public static IReadOnlyList<IApiKeyProvider> ScraperProviders => _scraperProviders.Value;

        /// <summary>
        /// Gets providers that are enabled for verifier use
        /// </summary>
        public static IReadOnlyList<IApiKeyProvider> VerifierProviders => _verifierProviders.Value;

        /// <summary>
        /// Gets providers for a specific bot type
        /// </summary>
        /// <param name="botType">The type of bot (Scraper or Verifier)</param>
        /// <returns>List of providers enabled for the specified bot type</returns>
        public static IReadOnlyList<IApiKeyProvider> GetProvidersForBot(BotType botType)
        {
            return botType switch
            {
                BotType.Scraper => ScraperProviders,
                BotType.Verifier => VerifierProviders,
                _ => Providers
            };
        }
    }

    /// <summary>
    /// Enumeration of bot types for provider filtering
    /// </summary>
    public enum BotType
    {
        Scraper,
        Verifier
    }
}
