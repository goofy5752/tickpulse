using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TickPulse.Infrastructure.Persistence;

/// <summary>Lets <c>dotnet ef</c> build the model without starting the API. No database is contacted.</summary>
internal sealed class TickPulseDbContextDesignTimeFactory : IDesignTimeDbContextFactory<TickPulseDbContext>
{
    public TickPulseDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TickPulseDbContext>()
            .UseNpgsql("Host=localhost;Database=tickpulse")
            .Options);
}
