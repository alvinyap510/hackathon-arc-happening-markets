using System.Numerics;
using Venue.Chain;
using Venue.Domain;

namespace Venue.Api.Endpoints;

/// <summary>Shared helpers for authenticated endpoint groups.</summary>
public static class EndpointHelpers
{
    /// <summary>Resolve the acting user from a Bearer session token (or the demo X-Demo-Address
    /// header in simulated mode). Returns null when unauthenticated.</summary>
    public static string? UserOf(HttpRequest req, SessionStore sessions, AppConfig cfg)
    {
        var auth = req.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var addr = sessions.Resolve(auth["Bearer ".Length..].Trim());
            if (addr != null) return addr;
        }
        if (cfg.Simulate && req.Headers.TryGetValue("X-Demo-Address", out var demo) && !string.IsNullOrWhiteSpace(demo))
            return Domain.Addresses.Normalize(demo.ToString());
        return null;
    }

    public static IResult RequireUser(string? user) => user == null ? Results.Unauthorized() : Results.Ok();

    /// <summary>Map a user-op submission to a REST result: tx hash on success, 400 on a
    /// contract rejection, 501 when the path needs a Circle SCA the host does not have.</summary>
    public static async Task<IResult> Submit(Func<Task<string>> submit)
    {
        try
        {
            var txHash = await submit();
            return Results.Ok(new { txHash });
        }
        catch (SimulationRevertException ex)
        {
            return Results.BadRequest(new { error = ex.Reason });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("SCA", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new { error = ex.Message }, statusCode: 501);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    public static BigInteger Amount(string? s) => BigInteger.TryParse(s, out var v) ? v : BigInteger.Zero;
}
