using Microsoft.EntityFrameworkCore;

namespace TickPulse.Infrastructure.Persistence;

/// <summary>
/// EF Core maps only <c>symbols</c>. Ticks, candles and the Timescale objects are created by raw SQL in
/// the migrations and accessed through Npgsql directly.
/// </summary>
public sealed class TickPulseDbContext(DbContextOptions<TickPulseDbContext> options) : DbContext(options)
{
    public const int SymbolNameMaxLength = 20;

    public DbSet<SymbolEntity> Symbols => Set<SymbolEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var symbol = modelBuilder.Entity<SymbolEntity>();
        symbol.ToTable("symbols");
        symbol.HasKey(s => s.Id);
        symbol.Property(s => s.Id).UseSerialColumn();
        symbol.Property(s => s.Name).HasMaxLength(SymbolNameMaxLength).IsRequired();
        symbol.HasIndex(s => s.Name).IsUnique();
        symbol.Property(s => s.BackfillStatus).HasColumnType("smallint");
    }
}
