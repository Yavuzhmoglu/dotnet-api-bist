using CoreApp.Models;
using Microsoft.Extensions.Caching.Memory;

namespace CoreApp.Services
{
    public class WhaleIntelService
    {
        private readonly IMemoryCache _cache;

        public WhaleIntelService(IMemoryCache cache)
        {
            _cache = cache;
        }

        private async Task<List<Candle>> GetCandlesCached(string symbol, string interval, string range)
        {
            var key = $"yahoo:{symbol}:{interval}:{range}";
            if (!_cache.TryGetValue(key, out List<Candle>? candles))
            {
                candles = await YahooClient.GetCandlesAsync(symbol, interval, range);
                _cache.Set(key, candles, TimeSpan.FromSeconds(10)); // 10 sn cache
            }
            return candles!;
        }

        public async Task<List<WhaleSignal>> GetWhaleSignalsAsync(string symbol, string interval, string range)
        {
            var candles = await GetCandlesCached(symbol, interval, range);
            var sigs = new List<WhaleSignal>();
            if (candles.Count < 60) return sigs;

            var vols = candles.Select(c => c.Volume).ToList();
            var rets = candles.Select(c => c.Return).ToList();

            for (int i = 1; i < candles.Count; i++)
            {
                var c = candles[i];

                // Rolling metrikler
                double vAvg20 = Indicators.SMA(vols, i, 20);
                double vStd20 = Indicators.Std(vols, i, 20);
                double rStd50 = Indicators.Std(rets, i, 50);
                if (double.IsNaN(vAvg20) || vAvg20 <= 0 || double.IsNaN(rStd50) || rStd50 <= 0) continue;

                double rvol = vols[i] / vAvg20;
                double bodyRatio = c.Body / c.Range;

                // Son 5 bar trend yönü (basit)
                double last5 = 0;
                if (i >= 5) last5 = rets.Skip(i - 5).Take(5).Sum();

                // --- Toplama (dipte büyük hacim, küçük gövde, önceki mini trend ↓)
                if (rvol >= 2.0 && bodyRatio <= 0.25 && last5 < 0)
                {
                    sigs.Add(Make(symbol, interval, c.Close.ToString("F2"), c.Open.ToString("F2"), c.Time, "Toplama",
                        $"Büyük hacim (RVOL≈{rvol:0.0}) + küçük mum; son 5 bar zayıf. Dipte balina topluyor olabilir."));
                    continue;
                }

                // --- Dağıtım (tepede büyük hacim, küçük gövde, önceki mini trend ↑)
                if (rvol >= 2.0 && bodyRatio <= 0.25 && last5 > 0)
                {
                    sigs.Add(Make(symbol, interval, c.Close.ToString("F2"), c.Open.ToString("F2"), c.Time, "Dağıtım",
                        $"Büyük hacim (RVOL≈{rvol:0.0}) + küçük mum; son 5 bar güçlü. Zirvede balina dağıtıyor olabilir."));
                    continue;
                }

                // --- Alış (momentum) : anormal yukarı getiri + yüksek hacim
                if (c.Return >= 2.0 * rStd50 && rvol >= 2.0)
                {
                    sigs.Add(Make(symbol, interval, c.Close.ToString("F2"), c.Open.ToString("F2"), c.Time, "Alış",
                        $"Anormal yukarı hareket (|r|≥2σ) ve yüksek hacim (RVOL≈{rvol:0.0}). Balina girişi sinyali."));
                    continue;
                }

                // --- Satış (momentum) : anormal aşağı getiri + yüksek hacim
                if (c.Return <= -2.0 * rStd50 && rvol >= 2.0)
                {
                    sigs.Add(Make(symbol, interval, c.Close.ToString("F2"), c.Open.ToString("F2"), c.Time, "Satış",
                        $"Anormal aşağı hareket (|r|≥2σ) ve yüksek hacim (RVOL≈{rvol:0.0}). Balina çıkışı sinyali."));
                    continue;
                }

                // --- CLIMAX UP/DOWN yorumu (gövde büyük + yüksek hacim)
                if (bodyRatio >= 0.7 && rvol >= 1.5)
                {
                    if (c.IsUp)
                        sigs.Add(Make(symbol, interval, c.Close.ToString("F2"), c.Open.ToString("F2"), c.Time, "Satış",
                            $"CLIMAX UP: Büyük gövde + yüksek hacim. Tepede kâr realizasyonu/dağıtım riski."));
                    else
                        sigs.Add(Make(symbol, interval, c.Close.ToString("F2"), c.Open.ToString("F2"), c.Time, "Alış",
                            $"CLIMAX DOWN: Büyük gövde + yüksek hacim. Dipte tepki/Toplama ihtimali."));
                    continue;
                }

                // --- Günlükte Gap + Hacim (yalnızca d)
                if (interval.EndsWith("d", StringComparison.OrdinalIgnoreCase) && i >= 15)
                {
                    var atr14 = Indicators.ATR(candles, i, 14);
                    if (!double.IsNaN(atr14) && atr14 > 0)
                    {
                        var prevClose = candles[i - 1].Close;
                        var gap = c.Open - prevClose;

                        if (gap >= 2.0 * atr14 && rvol >= 2.0)
                        {
                            sigs.Add(Make(symbol, interval, c.Close.ToString("F2"), c.Open.ToString("F2"), c.Time, "Alış",
                                $"Gap-Up (≥2×ATR) ve yüksek hacim (RVOL≈{rvol:0.0}). Momentum long."));
                            continue;
                        }
                        if (gap <= -2.0 * atr14 && rvol >= 2.0)
                        {
                            sigs.Add(Make(symbol, interval, c.Close.ToString("F2"), c.Open.ToString("F2"), c.Time, "Satış",
                                $"Gap-Down (≥2×ATR) ve yüksek hacim (RVOL≈{rvol:0.0}). Momentum short."));
                            continue;
                        }
                    }
                }
            }

            return sigs;
        }

        private static WhaleSignal Make(string sym, string intv, string value, string open, DateTime t, string action, string reason)
            => new WhaleSignal { Symbol = sym, Interval = intv, Value = value, Open = open, Time = t, Action = action, Reason = reason };
    }
}
