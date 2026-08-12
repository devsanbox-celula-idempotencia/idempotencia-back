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
    public DbSet<ProvisionedDatabaseDetail> ProvisionedDatabaseDetails => Set<ProvisionedDatabaseDetail>();
    public DbSet<DatabaseReservation> DatabaseReservations => Set<DatabaseReservation>();
    public DbSet<PlatformStatistics> PlatformStatistics => Set<PlatformStatistics>();

    // Conjuntos de resultados de los SPs del catálogo de DNS. Mismo criterio
    // que los de bases de datos: sin clave, solo lectura.
    public DbSet<DnsRecordInfo> DnsRecords => Set<DnsRecordInfo>();
    public DbSet<DnsRecordDetail> DnsRecordDetails => Set<DnsRecordDetail>();
    public DbSet<DnsRecordReservation> DnsRecordReservations => Set<DnsRecordReservation>();
    public DbSet<DnsRecordAdminInfo> DnsRecordAdminInfos => Set<DnsRecordAdminInfo>();

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
            // Tipo explícito del decimal: sin esto EF Core avisa que puede
            // truncar en silencio los valores de CurrentSizeMB que devuelve
            // sp_GetUserDatabases si exceden la precisión/escala por defecto.
            e.Property(p => p.CurrentSizeMB).HasColumnType("decimal(10,2)");
        });

        modelBuilder.Entity<DatabaseReservation>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
        });

        modelBuilder.Entity<ProvisionedDatabaseDetail>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
            // Mismo motivo que ProvisionedDatabaseInfo: sp_GetDatabaseDetail
            // también devuelve CurrentSizeMB como decimal.
            e.Property(p => p.CurrentSizeMB).HasColumnType("decimal(10,2)");
        });

        modelBuilder.Entity<PlatformStatistics>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
        });

        // DNS: los tres tipos son resultados de SP, igual que los de bases de
        // datos. No llevan configuración de decimales porque no tienen ninguno
        // (el TTL es un entero de segundos).
        modelBuilder.Entity<DnsRecordInfo>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
        });

        modelBuilder.Entity<DnsRecordDetail>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
        });

        modelBuilder.Entity<DnsRecordReservation>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
        });

        modelBuilder.Entity<DnsRecordAdminInfo>(e =>
        {
            e.HasNoKey();
            e.ToView(null);
        });
    }
}
