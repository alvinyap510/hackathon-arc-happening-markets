using Venue.Api.Endpoints;
using Xunit;

namespace Venue.Core.Tests;

/// <summary>
/// RFM auction-duration preset mapping (POST /v1/rfm/requests `duration` field). The preset
/// is the TOTAL commit+reveal span; commit = total - reveal with reveal = max(total/3, 20).
/// Omitted/null must preserve the configured default windows exactly (back-compat); any other
/// value must be rejected (the endpoint's HTTP 400 trigger).
/// </summary>
public class RfmDurationTests
{
    [Theory]
    [InlineData("1m", 60, 40, 20)]
    [InlineData("15m", 900, 600, 300)]
    [InlineData("1h", 3600, 2400, 1200)]
    [InlineData("4h", 14400, 9600, 4800)]
    [InlineData("24h", 86400, 57600, 28800)]
    public void Presets_MapToExactWindows(string preset, int total, int expectedCommit, int expectedReveal)
    {
        Assert.True(RfmEndpoints.TryResolveWindow(preset, 120, 60, out var commit, out var reveal));
        Assert.Equal(expectedCommit, commit);
        Assert.Equal(expectedReveal, reveal);
        Assert.Equal(total, commit + reveal);
    }

    [Theory]
    [InlineData("5m")]
    [InlineData("1M")]
    [InlineData("2h")]
    [InlineData("forever")]
    [InlineData("600")]
    public void UnknownOrNonPresetValue_IsRejected(string duration)
    {
        Assert.False(RfmEndpoints.TryResolveWindow(duration, 120, 60, out _, out _));
    }

    [Fact]
    public void OmittedDuration_PreservesConfiguredDefaults()
    {
        // Back-compat: null/empty duration must resolve to the exact configured windows.
        Assert.True(RfmEndpoints.TryResolveWindow(null, 120, 60, out var commit, out var reveal));
        Assert.Equal(120, commit);
        Assert.Equal(60, reveal);

        Assert.True(RfmEndpoints.TryResolveWindow("", 30, 15, out commit, out reveal));
        Assert.Equal(30, commit);
        Assert.Equal(15, reveal);
    }
}
