using CoreApp.Models;
using CoreApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CoreApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WhaleSignalsController : ControllerBase
    {
        private readonly WhaleIntelService _svc;
        private readonly AccumulationWeights _weights;

        public WhaleSignalsController(WhaleIntelService svc, IOptions<AccumulationWeights> weights)
        {
            _svc = svc;
            _weights = weights.Value;
        }

        /// <summary>
        /// Live "right now" signals: Toplama / Dağıtım / Alış / Satış / Bekle, one row per symbol,
        /// computed from data through the latest available bar only (no lookahead).
        /// </summary>
        /// <param name="symbols">Virgülle ayrılmış semboller (örn: ASELS.IS,THYAO.IS)</param>
        /// <param name="range">örn: 1y (varsayılan) -- sıkışma/CMF pencereleri için yeterli geçmiş gerekir</param>
        /// <param name="marketIndex">Piyasa trend filtresi için endeks sembolü (varsayılan XU100.IS)</param>
        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] string symbols, [FromQuery] string? range, [FromQuery] string? marketIndex)
        {
            if (string.IsNullOrWhiteSpace(symbols))
                return BadRequest(new { error = "symbols zorunlu. Örn: ASELS.IS,THYAO.IS" });

            var rng = string.IsNullOrWhiteSpace(range) ? "1y" : range!.Trim();
            var idx = string.IsNullOrWhiteSpace(marketIndex) ? "XU100.IS" : marketIndex!.Trim();

            // Fetched ONCE per request batch (cached ~90s), not once per symbol.
            var marketIndexCandles = await _svc.GetCandlesCachedAsync(idx, "1d", rng);

            var symbolList = symbols
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct()
                .Take(1000)
                .ToList();

            var bag = new ConcurrentBag<WhaleSignal>();

            // YahooClient's own SemaphoreSlim(2,2) still caps actual network concurrency;
            // this just stops cache-hit/CPU-bound symbols queuing behind one another.
            await Parallel.ForEachAsync(symbolList, new ParallelOptions { MaxDegreeOfParallelism = 8 },
                async (s, ct) =>
                {
                    try
                    {
                        var sig = await _svc.AnalyzeSymbolLiveAsync(s, rng, marketIndexCandles, _weights);
                        bag.Add(sig);
                    }
                    catch (Exception ex)
                    {
                        bag.Add(new WhaleSignal
                        {
                            Symbol = s,
                            Interval = "1d",
                            Time = DateTime.UtcNow,
                            Action = "Hata",
                            Reason = ex.Message,
                            DataSufficient = false
                        });
                    }
                });

            return Ok(bag.OrderBy(x => x.Time));
        }

        /// <summary>
        /// Offline strategy validation: grades historically-fired signals against forward returns.
        /// Kept entirely separate from the live endpoint so live scores can never depend on future data.
        /// </summary>
        [HttpGet("backtest")]
        public async Task<IActionResult> Backtest(
            [FromQuery] string symbols, [FromQuery] string? range, [FromQuery] int forwardDays = 3)
        {
            if (string.IsNullOrWhiteSpace(symbols))
                return BadRequest(new { error = "symbols zorunlu. Örn: ASELS.IS,THYAO.IS" });

            var rng = string.IsNullOrWhiteSpace(range) ? "2y" : range!.Trim();
            var symbolList = symbols
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct()
                .Take(50) // offline/manual tool -- kept small, not meant for 500-symbol batches
                .ToList();

            var all = new List<BacktestResult>();
            foreach (var s in symbolList)
            {
                try
                {
                    var res = await _svc.BacktestSymbolAsync(s, rng, forwardDays, _weights);
                    all.AddRange(res);
                }
                catch
                {
                    // Offline tool: skip symbols that fail to fetch rather than failing the whole batch.
                }
            }

            return Ok(all.OrderBy(x => x.SignalTime));
        }
    }
}
