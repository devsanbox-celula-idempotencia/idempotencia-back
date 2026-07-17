using idempotencia.Models;
using Microsoft.EntityFrameworkCore;

namespace idempotencia.Data;

public class ColmenaDbContext : DbContext
{
    public ColmenaDbContext(DbContextOptions<ColmenaDbContext> options)
        : base(options)
    {
    }

    // Conjuntos de resultados de los SPs (solo lectura, sin clave).
    public DbSet<LoginInfo> LoginInfos => Set<LoginInfo>();
    public DbSet<UserIdentity> UserIdentities => Set<UserIdentity>();
    public DbSet<ProvisionedDatabaseInfo> ProvisionedDatabases => Set<ProvisionedDatabaseInfo>();
    public DbSet<NewDatabaseResult> NewDatabaseResults => Set<NewDatabaseResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Se marcan como keyless: EF no los rastrea ni intenta crear tablas.
        // ToView(null) evita que EF asuma una tabla/vista física subyacente.
        modelBuilder.Entity<LoginInfo>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
        });

        modelBuilder.Entity<UserIdentity>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
        });

        modelBuilder.Entity<ProvisionedDatabaseInfo>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
        });

        modelBuilder.Entity<NewDatabaseResult>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
        });
    }
}
