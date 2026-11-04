using TickPulse.Application.Abstractions;

namespace TickPulse.Infrastructure.Persistence;

/// <summary>EF Core row for the <c>symbols</c> table.</summary>
public sealed class SymbolEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeactivatedAt { get; set; }

    public DateTime? ReactivatedAt { get; set; }

    public BackfillStatus BackfillStatus { get; set; }

    public DateTime? BackfilledFrom { get; set; }
}
