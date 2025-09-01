using System.Net;
using System.Text.Json;
using CoreApp.Models;
using Microsoft.Extensions.Caching.Memory;

namespace CoreApp.Services
{
    /// <summary>
    /// Günlük (1d) bazlı analiz servisi.
    /// - Yahoo (interval=1d&range=6mo) kullanır
    /// - RSI, MACD, Bollinger, OBV change, VWAP, Volume spike, Gap kontrolü
    /// - Manipülasyon skoru (0-100) ve Türkçe sinyal/tahmin üretir
    /// </summary>
    public class WhaleIntelService
    {
        private readonly IMemoryCache _cache;
        private readonly IHttpClientFactory _httpFactory;
        private const string YahooClientName = "yahoo";

        public WhaleIntelService(IMemoryCache cache, IHttpClientFactory httpFactory)
        {
            _cache = cache;
            _httpFactory = httpFactory;


        }

        // Public: ham JSON benzeri çıktı
        public async Task<object> AnalyzeDailySymbolRawAsync(string symbol)
        {
            var candles = await YahooClient.GetCandlesAsync(symbol, "1d", "1y");
            if (candles == null || candles.Count < 40)
                throw new InvalidOperationException("Yeterli günlük veri yok (>=40 bar gerekli).");

            int last = candles.Count - 1;
            var closes = candles.Select(c => c.Close).ToList();
            var vols = candles.Select(c => c.Volume).ToList();
            var typPrices = candles.Select(c => (c.High + c.Low + c.Close) / 3.0).ToList();

            // Indicators
            double rsi = Indicators.RSI(closes, 14);
            var macd = Indicators.MACD(closes, 12, 26, 9); // Value, Signal, Hist, Cross
            var bb = Indicators.Bollinger(closes, 20, 2.0); // Lower, Mid, Upper, WidthPct, PctB
            double obvChangePct = Indicators.OBVChangePercent(closes, vols, 10);
            double vwap = Indicators.AnchoredVWAP(typPrices, vols, 20);
            double vwapSapmaPct = double.IsNaN(vwap) || vwap == 0 ? 0 : (closes[last] - vwap) / vwap * 100.0;
            double hacimOrt10 = Indicators.SMA(vols, 10);
            double hacimSpikePct = hacimOrt10 > 0 ? (vols[last] - hacimOrt10) / hacimOrt10 * 100.0 : 0;
            double gapPct = candles.Count >= 2 ? (candles[last].Open - candles[last - 1].Close) / candles[last - 1].Close * 100.0 : 0;

            // Scoring
            int score = 0;
            var gerekceler = new List<string>();

            if (hacimSpikePct >= 50) { score += 20; gerekceler.Add($"Hacim spike: %{hacimSpikePct:F0} (10G ort. üzeri)"); }
            else if (hacimSpikePct >= 20) { score += 10; gerekceler.Add($"Hacim artışı: %{hacimSpikePct:F0}"); }

            if (obvChangePct >= 5) { score += 15; gerekceler.Add($"OBV artışı: %{obvChangePct:F1}"); }
            else if (obvChangePct <= -5) { score += 5; gerekceler.Add($"OBV düşüşü: %{obvChangePct:F1}"); }

            if (gapPct >= 1.5 && hacimSpikePct > 0) { score += 20; gerekceler.Add($"Gap yukarı %{gapPct:F1} + artan hacim"); }
            else if (gapPct <= -1.5 && hacimSpikePct > 0) { score += 10; gerekceler.Add($"Gap aşağı %{gapPct:F1} + artan hacim"); }

            if (rsi < 35) { score += 10; gerekceler.Add($"RSI düşük ({rsi:F1})"); }
            if (Indicators.RSIRebound(closes, 14)) { score += 15; gerekceler.Add("RSI aşağıdan yukarı dönüş sinyali"); }

            if (macd.Cross == "Yukarı") { score += 15; gerekceler.Add("MACD yukarı kesişim"); }
            else if (macd.Cross == "Aşağı") { score += 5; gerekceler.Add("MACD aşağı kesişim (risk)"); }

            double pctB = bb.PctB;
            if (pctB <= 0.2) { score += 15; gerekceler.Add("Bollinger alt bölgede (tepki ihtimali)"); }
            else if (pctB >= 0.8) { score += 5; gerekceler.Add("Bollinger üst bölgede (doygun)"); }

            if (vwapSapmaPct >= 2.5) { score += 10; gerekceler.Add($"VWAP üstü %{vwapSapmaPct:F1}"); }
            else if (vwapSapmaPct <= -2.5) { score += 10; gerekceler.Add($"VWAP altı %{vwapSapmaPct:F1}"); }

            score = Math.Min(100, score);

            // Signal logic (yukarıda verdiğin kurallar)
            bool bullBias =
                (rsi <= 40 || Indicators.RSIRebound(closes, 14)) &&
                (macd.Cross == "Yukarı" || macd.Value > 0) &&
                (pctB <= 0.6) &&
                (obvChangePct >= 0);

            bool strongBull = bullBias && (hacimSpikePct >= 20 || vwapSapmaPct > 0) && (gapPct >= 0 || pctB <= 0.4);

            string sinyal;
            string tahmin;
            if (strongBull && score >= 60)
            {
                sinyal = "Alış";
                tahmin = "Yarın veya 1-2 gün içinde yükseliş potansiyeli yüksek";
            }
            else if (bullBias && score >= 45)
            {
                sinyal = "Alış";
                tahmin = "1-2 gün içinde sınırlı yükseliş olası";
            }
            else if ((rsi >= 70 && macd.Cross == "Aşağı") || pctB >= 0.9)
            {
                sinyal = "Satış";
                tahmin = "Kâr realizasyonu / geri çekilme riski";
            }
            else
            {
                sinyal = "Bekle";
                tahmin = "Net avantaj yok; teyit beklenmeli";
            }

            var bollingerObj = new
            {
                Alt = bb.Lower,
                Orta = bb.Mid,
                Ust = bb.Upper,
                BantGenisligiYuzde = bb.WidthPct,
                PctB = pctB
            };

            var response = new
            {
                sembol = symbol,
                tarih = candles[last].Time.ToString("yyyy-MM-dd"),
                sonKapanis = Math.Round(closes[last], 6),
                sinyal,
                tahmin,
                manipulasyonSkoru = score,
                gerekceler,
                metrikler = new
                {
                    RSI = Math.Round(rsi, 2),
                    MACD = new
                    {
                        Deger = Math.Round(macd.Value, 6),
                        Sinyal = Math.Round(macd.Signal, 6),
                        Histogram = Math.Round(macd.Hist, 6),
                        Kesisim = macd.Cross
                    },
                    Bollinger = bollingerObj,
                    OBVDegisimYuzde = Math.Round(obvChangePct, 2),
                    VWAP = double.IsNaN(vwap) ? (double?)null : Math.Round(vwap, 6),
                    VWAPSapmaYuzde = Math.Round(vwapSapmaPct, 2),
                    HacimSpikeYuzde = Math.Round(hacimSpikePct, 2),
                    GapYuzde = Math.Round(gapPct, 2)
                }
            };

            return response;
        }

        // Public: WhaleSignal formatı ile (UI için)
        public async Task<WhaleSignal?> AnalyzeDailySymbolAsSignalAsync(string symbol)
        {
            var raw = await AnalyzeDailySymbolRawAsync(symbol);
            var doc = JsonSerializer.SerializeToElement(raw);
            var dict = doc.Deserialize<Dictionary<string, JsonElement>>()!;

            var sinyal = dict["sinyal"].GetString() ?? "Bekle";
            var score = dict["manipulasyonSkoru"].GetInt32();
            var tarih = dict["tarih"].GetString() ?? "";
            var sonKapanis = dict["sonKapanis"].GetDouble();
            var tahmin = dict["tahmin"].GetString() ?? "";
            var gerekcelerArr = dict["gerekceler"].EnumerateArray().Select(e => e.GetString()).Where(s => s != null).ToArray();

            return new WhaleSignal
            {
                Symbol = dict["sembol"].GetString() ?? symbol,
                Interval = "1d",
                Time = DateTime.Parse(tarih),
                Value = sonKapanis.ToString("F2"),
                Open = "",
                Action = sinyal,
                Reason = $"{tahmin}. " + string.Join(" | ", gerekcelerArr),
                Score = score,
                Confidence = Math.Clamp(score / 100.0, 0.25, 0.99),
                SuggestedAction = sinyal
            };
        }

        // ----- Data fetch + cache + retries -----
        private async Task<List<Candle>> GetDailyCandlesAsync(string symbol, string range = "6mo")
        {
            var cacheKey = $"yahoo:1d:{range}:{symbol}";
            if (_cache.TryGetValue(cacheKey, out List<Candle>? cached) && cached != null) return cached;

            var client = _httpFactory.CreateClient(YahooClientName);
            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{WebUtility.UrlEncode(symbol)}?interval=1d&range={range}";
            var json = await GetWithRetriesAsync(client, url);
            var candles = ParseYahooDaily(json);
            _cache.Set(cacheKey, candles, TimeSpan.FromSeconds(10));
            return candles;
        }

        private static async Task<string> GetWithRetriesAsync(HttpClient http, string url)
        {
            Exception? lastEx = null;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    using var resp = await http.GetAsync(url);
                    if ((int)resp.StatusCode == 429)
                    {
                        await Task.Delay(700 * (i + 1));
                        continue;
                    }
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsStringAsync();
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    await Task.Delay(500 * (i + 1));
                }
            }
            throw new HttpRequestException($"Yahoo isteği başarısız: {lastEx?.Message}");
        }

        private static List<Candle> ParseYahooDaily(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("chart", out var chart) || chart.GetProperty("result").GetArrayLength() == 0)
                throw new InvalidOperationException("Yahoo veri formatı beklenmeyen durumda.");

            var result0 = chart.GetProperty("result")[0];
            var timestamps = result0.GetProperty("timestamp").EnumerateArray().Select(x => x.GetInt64()).ToArray();
            var quote = result0.GetProperty("indicators").GetProperty("quote")[0];

            var opens = quote.GetProperty("open").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToArray();
            var highs = quote.GetProperty("high").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToArray();
            var lows = quote.GetProperty("low").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToArray();
            var closes = quote.GetProperty("close").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToArray();
            var volumes = quote.GetProperty("volume").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0d).ToArray();

            var list = new List<Candle>(timestamps.Length);
            for (int i = 0; i < timestamps.Length; i++)
            {
                if (double.IsNaN(opens[i]) || double.IsNaN(highs[i]) || double.IsNaN(lows[i]) || double.IsNaN(closes[i]))
                    continue;

                var dt = DateTimeOffset.FromUnixTimeSeconds(timestamps[i]).DateTime;
                list.Add(new Candle(dt, opens[i], highs[i], lows[i], closes[i], volumes[i]));
            }
            return list;
        }

        // ---------------- Indicators ----------------
        private static class Indicators
        {
            public static double SMA(IReadOnlyList<double> values, int period)
            {
                if (values.Count < period) return double.NaN;
                double sum = 0;
                for (int i = values.Count - period; i < values.Count; i++) sum += values[i];
                return sum / period;
            }

            public static double StdDev(IReadOnlyList<double> values, int period)
            {
                if (values.Count < period) return double.NaN;
                double mean = SMA(values, period);
                double sumSq = 0;
                for (int i = values.Count - period; i < values.Count; i++)
                {
                    var d = values[i] - mean;
                    sumSq += d * d;
                }
                return Math.Sqrt(sumSq / period);
            }

            public static double EMA(IReadOnlyList<double> values, int period)
            {
                if (values.Count < period) return double.NaN;
                double k = 2.0 / (period + 1);
                double ema = 0;
                for (int i = values.Count - period; i < values.Count; i++) ema += values[i];
                ema /= period;
                for (int i = values.Count - period + 1; i < values.Count; i++)
                    ema = (values[i] - ema) * k + ema;
                return ema;
            }

            public static double RSI(IReadOnlyList<double> closes, int period)
            {
                if (closes.Count < period + 1) return double.NaN;
                double gain = 0, loss = 0;
                for (int i = closes.Count - period; i < closes.Count; i++)
                {
                    double change = closes[i] - closes[i - 1];
                    if (change > 0) gain += change; else loss -= change;
                }
                gain /= period; loss /= period;
                if (loss == 0) return 100;
                double rs = gain / loss;
                return 100.0 - (100.0 / (1.0 + rs));
            }

            public static bool RSIRebound(IReadOnlyList<double> closes, int period)
            {
                if (closes.Count < period + 3) return false;
                var last = closes.Count - 1;
                var rsi1 = RSI(closes.Take(last).ToArray(), period);
                var rsi2 = RSI(closes.ToArray(), period);
                if (double.IsNaN(rsi1) || double.IsNaN(rsi2)) return false;
                return rsi1 < 30 && rsi2 > rsi1;
            }

            public static (double Value, double Signal, double Hist, string Cross) MACD(IReadOnlyList<double> closes, int fast = 12, int slow = 26, int signal = 9)
            {
                if (closes.Count < slow + signal) return (double.NaN, double.NaN, double.NaN, "Yok");
                double emaFast = EMA(closes, fast);
                double emaSlow = EMA(closes, slow);
                double macd = emaFast - emaSlow;

                var tail = closes.Skip(Math.Max(0, closes.Count - (slow + signal + 10))).ToList();
                var macdSeries = new List<double>();
                for (int i = 0; i < tail.Count; i++)
                {
                    var sub = tail.Take(i + 1).ToList();
                    var f = EMA(sub, fast);
                    var s = EMA(sub, slow);
                    macdSeries.Add(f - s);
                }
                double sig = EMA(macdSeries.ToArray(), signal);
                double hist = macd - sig;
                string cross = "Yok";
                if (macdSeries.Count >= 2)
                {
                    double prevMacd = macdSeries[^2];
                    double prevSig = EMA(macdSeries.Take(macdSeries.Count - 1).ToArray(), signal);
                    if (prevMacd <= prevSig && macd > sig) cross = "Yukarı";
                    else if (prevMacd >= prevSig && macd < sig) cross = "Aşağı";
                }
                return (macd, sig, hist, cross);
            }

            public static (double Lower, double Mid, double Upper, double WidthPct, double PctB) Bollinger(IReadOnlyList<double> closes, int period = 20, double mult = 2.0)
            {
                if (closes.Count < period) return (double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
                double mid = SMA(closes, period);
                double sd = StdDev(closes, period);
                double lower = mid - mult * sd;
                double upper = mid + mult * sd;
                double widthPct = mid != 0 ? (upper - lower) / mid * 100.0 : double.NaN;
                double lastClose = closes[^1];
                double pctB = upper == lower ? 0.5 : (lastClose - lower) / (upper - lower);
                return (lower, mid, upper, widthPct, pctB);
            }

            public static double OBVChangePercent(IReadOnlyList<double> closes, IReadOnlyList<double> vols, int lookback = 10)
            {
                if (closes.Count < lookback + 2) return 0;
                long obv = 0;
                for (int i = 1; i < closes.Count; i++)
                {
                    if (closes[i] > closes[i - 1]) obv += (long)vols[i];
                    else if (closes[i] < closes[i - 1]) obv -= (long)vols[i];
                }
                long obvPrev = 0;
                for (int i = 1; i < closes.Count - lookback; i++)
                {
                    if (closes[i] > closes[i - 1]) obvPrev += (long)vols[i];
                    else if (closes[i] < closes[i - 1]) obvPrev -= (long)vols[i];
                }
                if (obvPrev == 0) return 0;
                return (obv - obvPrev) / Math.Abs((double)obvPrev) * 100.0;
            }

            public static double AnchoredVWAP(IReadOnlyList<double> typical, IReadOnlyList<double> volume, int window = 20)
            {
                if (typical.Count < window) return double.NaN;
                double sumPV = 0, sumV = 0;
                for (int i = typical.Count - window; i < typical.Count; i++)
                {
                    sumPV += typical[i] * volume[i];
                    sumV += volume[i];
                }
                return sumV > 0 ? sumPV / sumV : typical[^1];
            }
        }
    }
}
