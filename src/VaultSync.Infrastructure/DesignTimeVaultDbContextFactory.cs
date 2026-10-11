using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VaultSync.Infrastructure;

public sealed class DesignTimeVaultDbContextFactory : IDesignTimeDbContextFactory<VaultDbContext>
{
    public VaultDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<VaultDbContext>()
            .UseSqlite("Data Source=vaultsync-design.db")
            .Options;
        return new(options);
    }
}
