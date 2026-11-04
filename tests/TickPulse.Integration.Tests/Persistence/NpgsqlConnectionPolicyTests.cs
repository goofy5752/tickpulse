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
        Mode(NpgsqlConnectionPolicy.Apply(Base, NpgsqlPolicySettings.Default)).ShouldBe(GssEncryptionMode.Disable);
    }

    [Theory]
    [InlineData("GSS Encryption Mode=Prefer")]
    [InlineData("GSS Encryption Mode=Require")]
    [InlineData("gss encryption mode=require")]
    [InlineData("GssEncryptionMode=Prefer")]
    [InlineData("GSS Encryption Mode=Disable")]
    public void Apply_AnyExplicitMode_WithoutTheOptIn_Disables(string setting)
    {
        Mode(NpgsqlConnectionPolicy.Apply($"{Base};{setting}", NpgsqlPolicySettings.Default)).ShouldBe(GssEncryptionMode.Disable);
    }

    [Theory]
    [InlineData("GSS Encryption Mode=Prefer", GssEncryptionMode.Prefer)]
    [InlineData("GSS Encryption Mode=Require", GssEncryptionMode.Require)]
    [InlineData("GssEncryptionMode=Require", GssEncryptionMode.Require)]
    [InlineData("GSS Encryption Mode=Disable", GssEncryptionMode.Disable)]
    public void Apply_ExplicitMode_WithTheOptIn_IsKept(string setting, GssEncryptionMode expected)
    {
        Mode(NpgsqlConnectionPolicy.Apply($"{Base};{setting}", new NpgsqlPolicySettings(AllowGssEncryption: true))).ShouldBe(expected);
    }

    [Fact]
    public void Apply_NoModeInTheConnectionString_WithTheOptIn_StillDisables()
    {
        // The opt-in only lets an explicit choice through; Npgsql's implicit default (Prefer) is never used.
        Mode(NpgsqlConnectionPolicy.Apply(Base, new NpgsqlPolicySettings(AllowGssEncryption: true))).ShouldBe(GssEncryptionMode.Disable);
    }

    [Fact]
    public void Apply_KeepsEveryOtherSetting()
    {
        var applied = new NpgsqlConnectionStringBuilder(NpgsqlConnectionPolicy.Apply(Base, NpgsqlPolicySettings.Default));

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
        var once = NpgsqlConnectionPolicy.Apply($"{Base};GSS Encryption Mode=Require", NpgsqlPolicySettings.Default);

        NpgsqlConnectionPolicy.Apply(once, NpgsqlPolicySettings.Default).ShouldBe(once);
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

        NpgsqlConnectionPolicy.Settings(configuration).AllowGssEncryption.ShouldBe(expected);
        NpgsqlConnectionPolicy.AllowGssEncryptionKey.ShouldBe("Database:AllowGssEncryption");
    }

    [Fact]
    public void Apply_NoPoolSizeInTheConnectionString_UsesTheConfiguredSize()
    {
        PoolSize(NpgsqlConnectionPolicy.Apply("Host=db", NpgsqlPolicySettings.Default)).ShouldBe(NpgsqlConnectionPolicy.DefaultMaxPoolSize);
        PoolSize(NpgsqlConnectionPolicy.Apply("Host=db", new NpgsqlPolicySettings(MaxPoolSize: 7))).ShouldBe(7);
        NpgsqlConnectionPolicy.DefaultMaxPoolSize.ShouldBe(20);
    }

    [Theory]
    [InlineData("Maximum Pool Size=50")]
    [InlineData("maximum pool size=50")]
    [InlineData("MaxPoolSize=50")]
    public void Apply_ExplicitPoolSize_IsKept(string setting)
    {
        PoolSize(NpgsqlConnectionPolicy.Apply($"Host=db;{setting}", new NpgsqlPolicySettings(MaxPoolSize: 7))).ShouldBe(50);
    }

    [Fact]
    public void Apply_MinimumAboveTheMaximum_Fails()
    {
        Should.Throw<InvalidOperationException>(() => NpgsqlConnectionPolicy.Apply("Host=db;Minimum Pool Size=30", NpgsqlPolicySettings.Default))
            .Message.ShouldContain("Minimum Pool Size (30)");
    }

    [Theory]
    [InlineData(null, 20)]
    [InlineData("", 20)]
    [InlineData("35", 35)]
    [InlineData(" 1 ", 1)]
    [InlineData("1024", 1024)]
    public void Settings_ReadsTheMaxPoolSize(string? value, int expected)
    {
        NpgsqlConnectionPolicy.Settings(Configuration(NpgsqlConnectionPolicy.MaxPoolSizeKey, value)).MaxPoolSize.ShouldBe(expected);
        NpgsqlConnectionPolicy.MaxPoolSizeKey.ShouldBe("Database:MaxPoolSize");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1025")]
    [InlineData("abc")]
    [InlineData("2.5")]
    public void Settings_InvalidMaxPoolSize_FailsWithTheKey(string value)
    {
        Should.Throw<InvalidOperationException>(() => NpgsqlConnectionPolicy.Settings(Configuration(NpgsqlConnectionPolicy.MaxPoolSizeKey, value)))
            .Message.ShouldContain("Database:MaxPoolSize");
    }

    [Fact]
    public void ConnectionBudget_IsBothPoolsPlusTheLeaderLock()
    {
        NpgsqlConnectionPolicy.ConnectionBudget(20, 4).ShouldBe(25);
    }

    private static int PoolSize(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString).MaxPoolSize;

    private static IConfiguration Configuration(string key, string? value) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

    [Theory]
    [InlineData("GSS Encryption Mode=Require", false, true)]
    [InlineData("GSS Encryption Mode=Prefer", false, true)]
    [InlineData("GSS Encryption Mode=Disable", false, false)]
    [InlineData("", false, false)]
    [InlineData("GSS Encryption Mode=Require", true, false)]
    public void OverridesExplicitMode_TellsWhenAConfiguredModeIsIgnored(string setting, bool allow, bool expected)
    {
        NpgsqlConnectionPolicy.OverridesExplicitMode($"{Base};{setting}", new NpgsqlPolicySettings(AllowGssEncryption: allow)).ShouldBe(expected);
    }
}
