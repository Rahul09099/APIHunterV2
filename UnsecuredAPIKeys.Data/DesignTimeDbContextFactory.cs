using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UnsecuredAPIKeys.Data
{
    /// <summary>
    /// Factory for creating DBContext during EF Core design-time operations (migrations).
    /// Uses SQLite for the lite version.
    /// </summary>
    public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DBContext>
    {
        public DBContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<DBContext>();
            var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");

            if (!string.IsNullOrEmpty(connectionString))
            {
                // Use Postgres for the factory if a connection string is provided
                // This ensures migrations are generated for Npgsql
                optionsBuilder.UseNpgsql(DBContext.ConvertPostgresUrl(connectionString));
            }
            else
            {
                // Fallback to SQLite for local development/Lite version
                optionsBuilder.UseSqlite("Data Source=unsecuredapikeys.db");
            }

            return new DBContext(optionsBuilder.Options);
        }
    }
}
