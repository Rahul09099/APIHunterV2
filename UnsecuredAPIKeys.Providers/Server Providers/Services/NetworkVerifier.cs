using System;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Providers.ServerProviders.Services
{
    public interface INetworkVerifier
    {
        Task<NetworkVerificationResult> VerifyConnectivityAsync(
            string host,
            int port,
            int timeoutSeconds = 10);
        
        Task<string> ExtractBannerAsync(
            string host,
            int port,
            int timeoutSeconds = 10);
        
        Task<string> PerformOSFingerprintingAsync(string host);
        Task<SslCertificateInfo> ExtractSslCertificateAsync(string host, int port);
    }

    public class NetworkVerifier : INetworkVerifier
    {
        private readonly ILogger<NetworkVerifier>? _logger;

        public NetworkVerifier(ILogger<NetworkVerifier>? logger = null)
        {
            _logger = logger;
        }

        public async Task<NetworkVerificationResult> VerifyConnectivityAsync(
            string host,
            int port,
            int timeoutSeconds = 10)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
                
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    return NetworkVerificationResult.Timeout();
                }
                
                await connectTask;

                if (client.Connected)
                {
                    return NetworkVerificationResult.Success(host, port);
                }
                
                return NetworkVerificationResult.Unreachable();
            }
            catch (SocketException ex)
            {
                _logger?.LogDebug("Socket error for {Host}:{Port} - {Error}", 
                    host, port, ex.Message);
                return NetworkVerificationResult.Unreachable();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Network verification error for {Host}:{Port}", 
                    host, port);
                return NetworkVerificationResult.Error(ex.Message);
            }
        }

        public async Task<string> ExtractBannerAsync(
            string host,
            int port,
            int timeoutSeconds = 10)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
                
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                if (completedTask == timeoutTask)
                {
                    return "Timeout";
                }
                
                await connectTask;

                if (!client.Connected)
                {
                    return "Unreachable";
                }
                
                using var stream = client.GetStream();
                stream.ReadTimeout = timeoutSeconds * 1000;
                
                var buffer = new byte[1024];
                var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                var readTimeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
                
                var completedRead = await Task.WhenAny(readTask, readTimeoutTask);
                if (completedRead == readTimeoutTask)
                {
                    return "No banner received (timeout)";
                }
                
                var bytesRead = await readTask;
                if (bytesRead > 0)
                {
                    return Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
                }
                
                return "No banner received";
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("Banner extraction failed for {Host}:{Port} - {Error}", 
                    host, port, ex.Message);
                return "Banner extraction failed";
            }
        }

        public async Task<string> PerformOSFingerprintingAsync(string host)
        {
            await Task.CompletedTask;
            return "Unknown";
        }

        public async Task<SslCertificateInfo> ExtractSslCertificateAsync(
            string host,
            int port)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
                
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                if (completedTask == timeoutTask)
                {
                    return SslCertificateInfo.Error("Timeout connecting");
                }
                await connectTask;

                if (!client.Connected)
                {
                    return SslCertificateInfo.Error("Unreachable");
                }
                
                using var sslStream = new SslStream(
                    client.GetStream(),
                    false,
                    (sender, certificate, chain, errors) => true);
                
                var authTask = sslStream.AuthenticateAsClientAsync(host);
                var authTimeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
                
                var completedAuth = await Task.WhenAny(authTask, authTimeoutTask);
                if (completedAuth == authTimeoutTask)
                {
                    return SslCertificateInfo.Error("Timeout during SSL Handshake");
                }
                await authTask;
                
                var cert = sslStream.RemoteCertificate as X509Certificate2;
                if (cert != null)
                {
                    return new SslCertificateInfo(
                        cert.Subject,
                        cert.Issuer,
                        cert.NotBefore,
                        cert.NotAfter,
                        cert.Thumbprint
                    );
                }
                
                return SslCertificateInfo.NotAvailable();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("SSL certificate extraction failed for {Host}:{Port} - {Error}", 
                    host, port, ex.Message);
                return SslCertificateInfo.Error(ex.Message);
            }
        }
    }
}
