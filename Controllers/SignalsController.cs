using CoreApp.Models;
using CoreApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

namespace CoreApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WhaleSignalsController : ControllerBase
    {
        private readonly WhaleIntelService _svc;
        public WhaleSignalsController(WhaleIntelService svc) => _svc = svc;

        /// <summary>
        /// Son kullanıcı odaklı sinyaller: Alış / Satış / Toplama / Dağıtım
        /// </summary>
        /// <param name="symbols">Virgülle ayrılmış semboller (örn: ASELS.IS,THYAO.IS)</param>
        /// <param name="interval">örn: 1m, 5m, 15m, 1h, 1d</param>
        /// <param name="range">örn: 1d, 5d, 1mo, 3mo, 1y</param>
        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] string symbols, [FromQuery] string? interval, [FromQuery] string? range)
        {
                if (string.IsNullOrWhiteSpace(symbols))
                return BadRequest(new { error = "symbols zorunlu. Örn: ASELS.IS,THYAO.IS" });

            var intv = string.IsNullOrWhiteSpace(interval) ? "1m" : interval!.Trim();
            var rng = string.IsNullOrWhiteSpace(range) ? "5d" : range!.Trim();

            var list = new List<WhaleSignal>();
            foreach (var s in symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(100))
            {
                try
                {
                    var sigs = await _svc.GetWhaleSignalsAsync(s, intv, rng);
                    list.AddRange(sigs);
                }
                catch (Exception ex)
                {
                    list.Add(new WhaleSignal
                    {
                        Symbol = s,
                        Interval = intv,
                        Time = DateTime.UtcNow,
                        Action = "Hata",
                        Reason = ex.Message
                    });
                }
            }

            return Ok(list.OrderBy(x => x.Time));
        }
    }
}