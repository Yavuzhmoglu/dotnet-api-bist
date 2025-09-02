using CoreApp.Models;
using CoreApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace CoreApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WhaleSignalsController : ControllerBase
    {
        private readonly WhaleIntelService _svc;
        public WhaleSignalsController(WhaleIntelService svc) => _svc = svc;

        /// <summary>
        /// Son kullanıcı odaklı sinyaller: Alış / Satış / Bekle
        /// </summary>
        /// <param name="symbols">Virgülle ayrılmış semboller (örn: ASELS.IS,THYAO.IS)</param>
        /// <param name="interval">örn: 1d (şimdilik yalnızca günlük destekleniyor)</param>
        /// <param name="range">örn: 6mo (şimdilik yalnızca 6 ay)</param>
        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] string symbols, [FromQuery] string? interval, [FromQuery] string? range)
        {
            if (string.IsNullOrWhiteSpace(symbols))
                return BadRequest(new { error = "symbols zorunlu. Örn: ASELS.IS,THYAO.IS" });

            var intv = string.IsNullOrWhiteSpace(interval) ? "1d" : interval!.Trim();
            var rng = string.IsNullOrWhiteSpace(range) ? "6mo" : range!.Trim();

            var list = new List<WhaleSignal>();

            foreach (var s in symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(1000))
            {
                try
                {
                    var sig = await _svc.AnalyzeDailySymbolSignalsAsync(s, intv, rng);
                    if (sig != null)
                    {
                        list.AddRange(sig);
                    }
                }
                catch (Exception ex)
                {
                    list.Add(new WhaleSignal
                    {
                        Symbol = s,
                        Interval = intv,
                        Time = DateTime.UtcNow,
                        Action = "Hata",
                        Reason = ex.Message,
                        Score = 0,
                        Confidence = 0
                    });
                }
            }

            return Ok(list.OrderBy(x => x.Time));
        }
    }
}
