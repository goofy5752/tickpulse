using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TickPulse.Infrastructure.Persistence.Migrations;

/// <summary>
/// <c>symbols.reactivated_at</c>: when a symbol was last reactivated. A reactivation's backfill ends the inactive
/// stretch at the first minute streamed since then, not at the first stream row after the deactivation (the minute the
/// old stream stopped in can lie after <c>deactivated_at</c> when its UNSUBSCRIBE took effect late).
/// </summary>
/// <remarks>Expand-only: a nullable column without a default. An older build ignores it.</remarks>
public partial class SymbolReactivatedAt : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "reactivated_at",
            table: "symbols",
            type: "timestamp with time zone",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "reactivated_at",
            table: "symbols");
    }
}
