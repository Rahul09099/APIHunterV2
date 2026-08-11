using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers.ServerProviders;
using UnsecuredAPIKeys.Services;

namespace UnsecuredAPIKeys.Tests
{
    public class IntegrationTests
    {
        private static DbContextOptions<DBContext> CreateNewContextOptions()
        {
            // Use unique SQLite in-memory database name per test run
            return new DbContextOptionsBuilder<DBContext>()
                .UseSqlite($"Data Source=InMemoryDb_{Guid.NewGuid():N};Mode=Memory;Cache=Shared")
                .Options;
        }

        [Fact]
        public async Task EndToEnd_Detection_Storage_And_Export_Pipeline()
        {
            // 1. Setup sqlite database connection in memory
            var options = CreateNewContextOptions();
            
            // EF Core SQLite in-memory needs an open connection to stay alive
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(options.FindExtension<Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal.SqliteOptionsExtension>()?.ConnectionString);
            await connection.OpenAsync();

            using (var db = new DBContext(options))
            {
                await db.Database.EnsureCreatedAsync();

                // 2. Arrange mock credentials and test parsing
                var provider = new ServerCredentialProvider();
                
                // Parse a mock FTP string
                var method = typeof(ServerCredentialProvider)
                    .GetMethod("ParseCredentialAndGetRawPassword", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                Assert.NotNull(method);

                var inputString = "ftp://ftp_admin:SecureFtpPass123@192.168.10.10:21";
                var tuple = method.Invoke(provider, new object[] { inputString });
                Assert.NotNull(tuple);

                var cred = tuple.GetType().GetField("Item1")?.GetValue(tuple) as ServerCredential;
                Assert.NotNull(cred);

                // Verify parser output
                Assert.Equal("FTP", cred.CredentialType);
                Assert.Equal("ftp_admin", cred.Username);
                Assert.Equal("192.168.10.10", cred.Host);
                Assert.Equal(21, cred.Port);
                Assert.NotEmpty(cred.Password);
                
                // Verify that the plaintext password is saved
                Assert.Equal("SecureFtpPass123", cred.Password);

                // Populate other fields for roundtrip validation
                cred.NetworkStatus = "Accessible";
                cred.AuthenticationStatus = "Valid";
                cred.RiskLevel = "High";
                cred.IsHoneypot = false;
                cred.ServerMetadata = "{\"banner\":\"MockFTP 1.0\"}";
                cred.GeolocationData = "{\"Country\":\"US\",\"ISP\":\"LocalISP\"}";
                cred.OSINTData = "{\"greyNoiseClassification\":\"benign\"}";
                cred.SourceRepository = "https://github.com/user/repo";
                cred.SourceFilePath = "src/config.py";

                // Save to database
                await db.ServerCredentials.AddAsync(cred);
                await db.SaveChangesAsync();

                // 3. Query back from Database and Verify
                var retrieved = await db.ServerCredentials.FirstOrDefaultAsync(c => c.Username == "ftp_admin");
                Assert.NotNull(retrieved);
                Assert.Equal("FTP", retrieved.CredentialType);
                Assert.Equal("192.168.10.10", retrieved.Host);
                Assert.Equal(21, retrieved.Port);
                Assert.Equal("Accessible", retrieved.NetworkStatus);
                Assert.Equal("Valid", retrieved.AuthenticationStatus);
                Assert.Equal("High", retrieved.RiskLevel);
                Assert.Equal("{\"banner\":\"MockFTP 1.0\"}", retrieved.ServerMetadata);
                Assert.Equal("{\"Country\":\"US\",\"ISP\":\"LocalISP\"}", retrieved.GeolocationData);
                Assert.Equal("https://github.com/user/repo", retrieved.SourceRepository);

                // 4. Test Export System
                var dbService = new DatabaseService("unsecuredapikeys.db"); // temporary wrapper paths
                var jsonPath = Path.GetTempFileName();
                var csvPath = Path.GetTempFileName();

                try
                {
                    // Export to JSON
                    await dbService.ExportServerCredentialsAsync(db, jsonPath, "json");
                    Assert.True(File.Exists(jsonPath));
                    var jsonContent = await File.ReadAllTextAsync(jsonPath);
                    Assert.Contains("ftp_admin", jsonContent);
                    Assert.Contains("192.168.10.10", jsonContent);
                    Assert.Contains("DiscoveredAtIST", jsonContent);

                    // Export to CSV
                    await dbService.ExportServerCredentialsAsync(db, csvPath, "csv");
                    Assert.True(File.Exists(csvPath));
                    var csvContent = await File.ReadAllTextAsync(csvPath);
                    
                    // Verify headers and row elements
                    Assert.Contains("CredentialType,Host,Port,Username", csvContent);
                    Assert.Contains("ftp_admin", csvContent);
                    Assert.Contains("192.168.10.10", csvContent);
                    
                    // Verify flattened JSON columns in CSV
                    Assert.Contains("banner: MockFTP 1.0", csvContent);
                    Assert.Contains("Country: US | ISP: LocalISP", csvContent);
                }
                finally
                {
                    if (File.Exists(jsonPath)) File.Delete(jsonPath);
                    if (File.Exists(csvPath)) File.Delete(csvPath);
                }
            }
        }
    }
}
