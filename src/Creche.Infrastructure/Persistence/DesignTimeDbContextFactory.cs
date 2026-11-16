using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Creche.Infrastructure.Persistence;

/// <summary>Used only by the dotnet-ef tool (see scripts/add-migration.ps1); no real database is needed.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CrecheDbContext>
{
    public CrecheDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<CrecheDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
