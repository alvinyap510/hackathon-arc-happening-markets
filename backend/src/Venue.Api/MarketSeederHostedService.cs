using Venue.Chain;
using Venue.Domain;
using Venue.Infrastructure;

namespace Venue.Api;

/// <summary>
/// SEAM 2: seed two markets on-chain at startup so the Markets tab renders in real mode.
/// For each seed, persist the G1 metadata (keyed by the deterministic marketId) and submit
/// an operator createMarket. Idempotent across restarts: if the market already exists in
/// the replayed core state, skip the on-chain tx (which would revert AlreadyExists).
/// </summary>
public sealed class MarketSeederHostedService(VenueCore core, IChainGateway gateway, MarketMetadataStore metadata, AppConfig cfg) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        // Let the indexer connect and replay first; then seed.
        await Task.Delay(2000, ct);
        if (!cfg.SeedMarketsEnabled) return;
        foreach (var seed in cfg.SeedMarkets)
        {
            var marketId = Hash.KeccakHex(seed.QuestionText);
            metadata.Save(marketId, seed.QuestionText, seed.ResolutionSource, seed.CloseTime);

            var exists = true;
            try
            {
                await core.GetMarketAsync(marketId);
            }
            catch (KeyNotFoundException)
            {
                exists = false;
            }
            if (!exists)
            {
                await gateway.SubmitCreateMarketAsync(marketId, System.Text.Encoding.UTF8.GetBytes(seed.QuestionText), ct);
                Console.WriteLine($"seeded market {marketId}");
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
