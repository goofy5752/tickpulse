using Microsoft.Extensions.Configuration;
using Npgsql;
using TickPulse.Infrastructure.Persistence;

namespace TickPulse.Integration.Tests.Persistence;

// Pure connection-string tests: no database is contacted.
public sealed class NpgsqlConnectionPolicyTests
{
    private const string Base = "Host=db;Port=5432;Database=tickpulse;Username=tickpulse;Password=secret;Maximum Pool Size=20";

    private static GssEncryptionMode Mode(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString).GssEncryptionMode;

    [Fact]
    public void Apply_NoModeInTheConnectionString_Disables()
    {
        Mode(NpgsqlConnectionPolicy.Apply(Base, allowGssEncryption: false)).ShouldBe(GssEncryptionMode.Disable);
    }

    [Theory]
    [InlineData("GSS Encryption Mode=Prefer")]
    [InlineData("GSS Encryption Mode=Require")]
    [InlineData("gss encryption mode=require")]
    [InlineData("GssEncryptionMode=Prefer")]
    [InlineData("GSS Encryption Mode=Disable")]
    public void Apply_AnyExplicitMode_WithoutTheOptIn_Disables(string setting)
    {
        Mode(NpgsqlConnectionPolicy.Apply($"{Base};{setting}", allowGssEncryption: false)).ShouldBe(GssEncryptionMode.Disable);
    }

    [Theory]
    [InlineData("GSS Encryption Mode=Prefer", GssEncryptionMode.Prefer)]
    [InlineData("GSS Encryption Mode=Require", GssEncryptionMode.Require)]
    [InlineData("GssEncryptionMode=Require", GssEncryptionMode.Require)]
    [InlineData("GSS Encryption Mode=Disable", GssEncryptionMode.Disable)]
    public void Apply_ExplicitMode_WithTheOptIn_IsKept(string setting, GssEncryptionMode expected)
    {
        Mode(NpgsqlConnectionPolicy.Apply($"{Base};{setting}", allowGssEncryption: true)).ShouldBe(expected);
    }

    [Fact]
    public void Apply_NoModeInTheConnectionString_WithTheOptIn_StillDisables()
    {
        // The opt-in only lets an explicit choice through; Npgsql's implicit default (Prefer) is never used.
        Mode(NpgsqlConnectionPolicy.Apply(Base, allowGssEncryption: true)).ShouldBe(GssEncryptionMode.Disable);
    }

    [Fact]
    public void Apply_KeepsEveryOtherSetting()
    {
        var applied = new NpgsqlConnectionStringBuilder(NpgsqlConnectionPolicy.Apply(Base, allowGssEncryption: false));

        applied.Host.ShouldBe("db");
        applied.Port.ShouldBe(5432);
        applied.Database.ShouldBe("tickpulse");
        applied.Username.ShouldBe("tickpulse");
        applied.Password.ShouldBe("secret");
        applied.MaxPoolSize.ShouldBe(20);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var once = NpgsqlConnectionPolicy.Apply($"{Base};GSS Encryption Mode=Require", allowGssEncryption: false);

        NpgsqlConnectionPolicy.Apply(once, allowGssEncryption: false).ShouldBe(once);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    public void AllowGssEncryption_ReadsTheOptInKey_DefaultFalse(string? value, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [NpgsqlConnectionPolicy.AllowGssEncryptionKey] = value })
            .Build();

        NpgsqlConnectionPolicy.AllowGssEncryption(configuration).ShouldBe(expected);
        NpgsqlConnectionPolicy.AllowGssEncryptionKey.ShouldBe("Database:AllowGssEncryption");
    }

    [Theory]
    [InlineData("GSS Encryption Mode=Require", false, true)]
    [InlineData("GSS Encryption Mode=Prefer", false, true)]
    [InlineData("GSS Encryption Mode=Disable", false, false)]
    [InlineData("", false, false)]
    [InlineData("GSS Encryption Mode=Require", true, false)]
    public void OverridesExplicitMode_TellsWhenAConfiguredModeIsIgnored(string setting, bool allow, bool expected)
    {
        NpgsqlConnectionPolicy.OverridesExplicitMode($"{Base};{setting}", allow).ShouldBe(expected);
    }
}
