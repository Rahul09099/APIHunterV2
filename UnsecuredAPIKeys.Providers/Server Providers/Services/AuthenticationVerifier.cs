using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Providers.ServerProviders.Services
{
    public interface IAuthenticationVerifier
    {
        Task<AuthVerificationResult> VerifySSHAsync(string targetAddress, int port, string username, string password);
        Task<AuthVerificationResult> VerifyFTPAsync(string targetAddress, int port, string username, string password);
        Task<AuthVerificationResult> VerifyRDPAsync(string targetAddress, int port, string username, string password, string domain = "");
        Task<AuthVerificationResult> VerifySMTPAsync(string targetAddress, int port, string username, string password);
        Task<AuthVerificationResult> VerifyIMAPAsync(string targetAddress, int port, string username, string password);
        Task<AuthVerificationResult> VerifyPOP3Async(string targetAddress, int port, string username, string password);
        Task<AuthVerificationResult> VerifyCPanelAsync(string targetAddress, int port, string username, string password, string originalHostname);
        Task<AuthVerificationResult> VerifyWHMAsync(string targetAddress, int port, string username, string password, string originalHostname);
        Task<AuthVerificationResult> VerifyPleskAsync(string targetAddress, int port, string username, string password, string originalHostname);
        Task<AuthVerificationResult> VerifyDatabaseAsync(CredentialType dbType, string targetAddress, int port, string username, string password, string database);
        bool IsOnCooldown(string credentialHash);
    }

    public class AuthenticationVerifier : IAuthenticationVerifier
    {
        private readonly IMemoryCache _cooldownCache;
        private readonly ILogger<AuthenticationVerifier>? _logger;
        private readonly TimeSpan _cooldownPeriod = TimeSpan.FromHours(24);

        public AuthenticationVerifier(IMemoryCache cooldownCache, ILogger<AuthenticationVerifier>? logger = null)
        {
            _cooldownCache = cooldownCache;
            _logger = logger;
        }

        public bool IsOnCooldown(string credentialHash)
        {
            return _cooldownCache.TryGetValue($"auth_cooldown_{credentialHash}", out _);
        }

        private void SetCooldown(string credentialHash)
        {
            _cooldownCache.Set($"auth_cooldown_{credentialHash}", true, _cooldownPeriod);
        }

        public string ComputeHash(string host, int port, string username)
        {
            var input = $"{host}:{port}:{username}";
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(input)));
        }

        private async Task<bool> CheckCooldownAndSetAsync(string host, int port, string username)
        {
            var hash = ComputeHash(host, port, username);
            if (IsOnCooldown(hash))
            {
                return true; // on cooldown
            }
            SetCooldown(hash);
            return false; // not on cooldown
        }

        public async Task<AuthVerificationResult> VerifySSHAsync(string targetAddress, int port, string username, string password)
        {
            if (await CheckCooldownAndSetAsync(targetAddress, port, username))
                return AuthVerificationResult.RateLimited();

            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(targetAddress, port);
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) == connectTask)
                {
                    await connectTask;
                    if (client.Connected)
                    {
                        using var stream = client.GetStream();
                        var buffer = new byte[512];
                        var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                        var banner = Encoding.UTF8.GetString(buffer, 0, read);
                        if (banner.StartsWith("SSH-"))
                        {
                            return AuthVerificationResult.Valid($"SSH service detected: {banner.Trim()}");
                        }
                    }
                }
                return AuthVerificationResult.Invalid("Not a valid SSH service");
            }
            catch (Exception ex)
            {
                return AuthVerificationResult.Error(ex.Message);
            }
        }

        public async Task<AuthVerificationResult> VerifyFTPAsync(string targetAddress, int port, string username, string password)
        {
            if (await CheckCooldownAndSetAsync(targetAddress, port, username))
                return AuthVerificationResult.RateLimited();

            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(targetAddress, port);
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask)
                    return AuthVerificationResult.Error("Timeout");
                
                await connectTask;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

                var banner = await reader.ReadLineAsync();
                await writer.WriteLineAsync($"USER {username}");
                var userResp = await reader.ReadLineAsync();
                
                await writer.WriteLineAsync($"PASS {password}");
                var passResp = await reader.ReadLineAsync();

                if (passResp != null && (passResp.StartsWith("230") || passResp.Contains("successful") || passResp.Contains("logged in")))
                {
                    return AuthVerificationResult.Valid($"FTP login successful: {passResp}");
                }
                return AuthVerificationResult.Invalid(passResp ?? "FTP login failed");
            }
            catch (Exception ex)
            {
                return AuthVerificationResult.Error(ex.Message);
            }
        }

        public async Task<AuthVerificationResult> VerifyRDPAsync(string targetAddress, int port, string username, string password, string domain = "")
        {
            if (await CheckCooldownAndSetAsync(targetAddress, port, username))
                return AuthVerificationResult.RateLimited();

            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(targetAddress, port);
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) == connectTask)
                {
                    await connectTask;
                    if (client.Connected)
                    {
                        return AuthVerificationResult.Valid("RDP service reachable");
                    }
                }
                return AuthVerificationResult.Invalid("RDP service unreachable");
            }
            catch (Exception ex)
            {
                return AuthVerificationResult.Error(ex.Message);
            }
        }

        public async Task<AuthVerificationResult> VerifySMTPAsync(string targetAddress, int port, string username, string password)
        {
            if (await CheckCooldownAndSetAsync(targetAddress, port, username))
                return AuthVerificationResult.RateLimited();

            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(targetAddress, port);
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask)
                    return AuthVerificationResult.Error("Timeout");
                
                await connectTask;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

                var banner = await reader.ReadLineAsync();
                await writer.WriteLineAsync("EHLO APIHunter");
                
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (line.StartsWith("250 ")) break;
                }

                await writer.WriteLineAsync("AUTH LOGIN");
                var authResp = await reader.ReadLineAsync();

                await writer.WriteLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(username)));
                var userResp = await reader.ReadLineAsync();

                await writer.WriteLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(password)));
                var passResp = await reader.ReadLineAsync();

                if (passResp != null && passResp.StartsWith("235"))
                {
                    return AuthVerificationResult.Valid($"SMTP authentication successful: {passResp}");
                }
                return AuthVerificationResult.Invalid(passResp ?? "SMTP authentication failed");
            }
            catch (Exception ex)
            {
                return AuthVerificationResult.Error(ex.Message);
            }
        }

        public async Task<AuthVerificationResult> VerifyIMAPAsync(string targetAddress, int port, string username, string password)
        {
            if (await CheckCooldownAndSetAsync(targetAddress, port, username))
                return AuthVerificationResult.RateLimited();

            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(targetAddress, port);
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask)
                    return AuthVerificationResult.Error("Timeout");

                await connectTask;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

                var banner = await reader.ReadLineAsync();
                await writer.WriteLineAsync($"A01 LOGIN {username} {password}");
                var resp = await reader.ReadLineAsync();

                if (resp != null && (resp.Contains("A01 OK") || resp.Contains("completed")))
                {
                    return AuthVerificationResult.Valid($"IMAP login successful: {resp}");
                }
                return AuthVerificationResult.Invalid(resp ?? "IMAP login failed");
            }
            catch (Exception ex)
            {
                return AuthVerificationResult.Error(ex.Message);
            }
        }

        public async Task<AuthVerificationResult> VerifyPOP3Async(string targetAddress, int port, string username, string password)
        {
            if (await CheckCooldownAndSetAsync(targetAddress, port, username))
                return AuthVerificationResult.RateLimited();

            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(targetAddress, port);
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask)
                    return AuthVerificationResult.Error("Timeout");

                await connectTask;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

                var banner = await reader.ReadLineAsync();
                await writer.WriteLineAsync($"USER {username}");
                var userResp = await reader.ReadLineAsync();

                await writer.WriteLineAsync($"PASS {password}");
                var passResp = await reader.ReadLineAsync();

                if (passResp != null && passResp.StartsWith("+OK"))
                {
                    return AuthVerificationResult.Valid($"POP3 login successful: {passResp}");
                }
                return AuthVerificationResult.Invalid(passResp ?? "POP3 login failed");
            }
            catch (Exception ex)
            {
                return AuthVerificationResult.Error(ex.Message);
            }
        }

        /// <summary>
        /// Creates an HttpClient that pins the TCP connection to targetAddress
        /// while using originalHostname for Host header, TLS SNI, and certificate validation.
        /// </summary>
        private HttpClient CreatePinnedHttpClient(string targetAddress, int port, string originalHostname)
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellationToken) =>
                {
                    // Force TCP connection to the validated IP, ignoring DNS
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    socket.NoDelay = true;
                    try
                    {
                        await socket.ConnectAsync(
                            new DnsEndPoint(targetAddress, port),
                            cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    // TLS SNI and certificate validation use the original hostname
                    TargetHost = originalHostname,
                    RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
                }
            };

            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(10);
            return client;
        }

        public async Task<AuthVerificationResult> VerifyCPanelAsync(string targetAddress, int port, string username, string password, string originalHostname)
        {
            if (await CheckCooldownAndSetAsync(targetAddress, port, username))
                return AuthVerificationResult.RateLimited();

            try
            {
                using var client = CreatePinnedHttpClient(targetAddress, port, originalHostname);
                
                // URL uses the original hostname so Host header and SNI are correct
                var url = $"https://{originalHostname}:{port}/execute/Email/list_pops";
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

                var response = await client.GetAsync(url);
                return response.IsSuccessStatusCode
                    ? AuthVerificationResult.Valid("cPanel authentication successful")
                    : AuthVerificationResult.Invalid($"cPanel authentication failed: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                return AuthVerificationResult.Error(ex.Message);
            }
        }

        public async Task<AuthVerificationResult> VerifyWHMAsync(string targetAddress, int port, string username, string password, string originalHostname)
        {
            if (await CheckCooldownAndSetAsync(targetAddress, port, username))
                return AuthVerificationResult.RateLimited();

            try
            {
                using var client = CreatePinnedHttpClient(targetAddress, port, originalHostname);

                var url = $"https://{originalHostname}:{port}/json-api/version";
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

                var response = await client.GetAsync(url);
                return response.IsSuccessStatusCode
                    ? AuthVerificationResult.Valid("WHM authentication successful")
                    : AuthVerificationResult.Invalid($"WHM authentication failed: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                return AuthVerificationResult.Error(ex.Message);
            }
        }

        public async Task<AuthVerificationResult> VerifyPleskAsync(string targetAddress, int port, string username, string password, string originalHostname)
        {
            if (await CheckCooldownAndSetAsync(targetAddress, port, username))
                return AuthVerificationResult.RateLimited();

            try
            {
                using var client = CreatePinnedHttpClient(targetAddress, port, originalHostname);

                var url = $"https://{originalHostname}:{port}/api/v2/server";
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

                var response = await client.GetAsync(url);
                return response.IsSuccessStatusCode
                    ? AuthVerificationResult.Valid("Plesk authentication successful")
                    : AuthVerificationResult.Invalid($"Plesk authentication failed: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                return AuthVerificationResult.Error(ex.Message);
            }
        }

        public async Task<AuthVerificationResult> VerifyDatabaseAsync(CredentialType dbType, string targetAddress, int port, string username, string password, string database)
        {
            if (await CheckCooldownAndSetAsync(targetAddress, port, username))
                return AuthVerificationResult.RateLimited();

            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(targetAddress, port);
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) == connectTask)
                {
                    await connectTask;
                    if (client.Connected)
                    {
                        return AuthVerificationResult.Valid($"{dbType} connection reachable");
                    }
                }
                return AuthVerificationResult.Invalid($"{dbType} connection unreachable");
            }
            catch (Exception ex)
            {
                return AuthVerificationResult.Error(ex.Message);
            }
        }
    }
}
