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

            // Use SQLite for the lite version
            optionsBuilder.UseSqlite("Data Source=unsecuredapikeys.db");

            return new DBContext(optionsBuilder.Options);
        }
    }
}
