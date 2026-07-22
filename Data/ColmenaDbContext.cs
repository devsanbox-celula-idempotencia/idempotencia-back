using idempotencia.Models;
using Microsoft.EntityFrameworkCore;

namespace idempotencia.Data;

/// <summary>
/// DbContext de Colmena. Se usa EXCLUSIVAMENTE como mapeador para ejecutar
/// Stored Procedures (arquitectura database-centric). Todos los tipos son "sin
/// clave" (<c>HasNoKey()</c>) porque solo representan conjuntos de resultados de
/// los SPs, no tablas de escritura.
/// </summary>
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
    public DbSet<DatabaseReservation> DatabaseReservations => Set<DatabaseReservation>();
    public DbSet<PlatformStatistics> PlatformStatistics => Set<PlatformStatistics>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // keyless: EF no rastrea ni intenta crear tablas. ToView(null) evita que
        // asuma una tabla/vista física subyacente.
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

        modelBuilder.Entity<DatabaseReservation>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
        });

        modelBuilder.Entity<PlatformStatistics>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
        });
    }
}
