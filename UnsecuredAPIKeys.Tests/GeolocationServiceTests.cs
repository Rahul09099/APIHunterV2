using System.Threading.Tasks;
using Xunit;
using UnsecuredAPIKeys.Providers.ServerProviders.Services;

namespace UnsecuredAPIKeys.Tests
{
    public class GeolocationServiceTests
    {
        private readonly GeolocationService _service = new();

        [Fact]
        public void IsCloudProviderIP_KnownAwsIP_ReturnsAWS()
        {
            var isCloud = _service.IsCloudProviderIP("3.5.10.15", out var provider);
            Assert.True(isCloud);
            Assert.Equal("AWS", provider);
        }

        [Fact]
        public void IsCloudProviderIP_KnownGcpIP_ReturnsGCP()
        {
            var isCloud = _service.IsCloudProviderIP("34.64.100.2", out var provider);
            Assert.True(isCloud);
            Assert.Equal("GCP", provider);
        }

        [Fact]
        public void IsCloudProviderIP_PrivateIP_ReturnsFalse()
        {
            var isCloud = _service.IsCloudProviderIP("192.168.1.1", out var provider);
            Assert.False(isCloud);
            Assert.Empty(provider);
        }

        [Fact]
        public async Task GeolocateAsync_InvalidOrPrivateIP_ReturnsGracefulFallback()
        {
            var result1 = await _service.GeolocateAsync("invalid-ip");
            Assert.Equal("Geolocation unavailable", result1.Message);

            var result2 = await _service.GeolocateAsync("127.0.0.1");
            Assert.Equal("Private Network", result2.Country);
            Assert.Equal("Local", result2.City);
        }
    }
}
