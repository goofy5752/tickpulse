using System.Net;
using Microsoft.Extensions.Time.Testing;
using TickPulse.Api.Telemetry;

namespace TickPulse.Integration.Tests.Api;

public sealed class ClientIpHasherTests
{
    private static readonly DateTimeOffset Morning = new(2026, 3, 2, 0, 0, 1, TimeSpan.Zero);
    private static readonly IPAddress Visitor = IPAddress.Parse("198.51.100.23");

    [Fact]
    public void Hash_IsTwelveLowercaseHexChars_WithoutTheIp()
    {
        var hash = new ClientIpHasher("salt", new FakeTimeProvider(Morning)).Hash(Visitor);

        hash.Length.ShouldBe(12);
        hash.ShouldMatch("^[0-9a-f]{12}$");
        hash.ShouldNotContain("198");
    }

    [Fact]
    public void Hash_SameDay_IsStable()
    {
        var clock = new FakeTimeProvider(Morning);
        var hasher = new ClientIpHasher("salt", clock);
        var first = hasher.Hash(Visitor);

        clock.Advance(TimeSpan.FromHours(23));

        hasher.Hash(Visitor).ShouldBe(first);
    }

    [Fact]
    public void Hash_NextUtcDay_Rotates()
    {
        var clock = new FakeTimeProvider(Morning);
        var hasher = new ClientIpHasher("salt", clock);
        var first = hasher.Hash(Visitor);

        clock.Advance(TimeSpan.FromDays(1));

        hasher.Hash(Visitor).ShouldNotBe(first);
    }

    [Fact]
    public void Hash_DifferentSaltOrIp_Differs()
    {
        var clock = new FakeTimeProvider(Morning);

        new ClientIpHasher("a", clock).Hash(Visitor).ShouldNotBe(new ClientIpHasher("b", clock).Hash(Visitor));
        new ClientIpHasher("a", clock).Hash(Visitor).ShouldNotBe(new ClientIpHasher("a", clock).Hash(IPAddress.Parse("198.51.100.24")));
    }

    [Fact]
    public void Hash_Ipv4MappedToIpv6_EqualsIpv4()
    {
        var hasher = new ClientIpHasher("salt", new FakeTimeProvider(Morning));

        hasher.Hash(Visitor.MapToIPv6()).ShouldBe(hasher.Hash(Visitor));
    }

    [Fact]
    public void Hash_NoAddress_IsUnknown() =>
        new ClientIpHasher("salt", new FakeTimeProvider(Morning)).Hash(null).ShouldBe(ClientIpHasher.Unknown);

    [Fact]
    public void Hash_NoSaltConfigured_UsesRandomSaltPerInstance()
    {
        var clock = new FakeTimeProvider(Morning);

        new ClientIpHasher(null, clock).Hash(Visitor).ShouldNotBe(new ClientIpHasher("", clock).Hash(Visitor));
    }
}
