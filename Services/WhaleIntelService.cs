using CoreApp.Models;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CoreApp.Services
{
    public class WhaleIntelService
    {
        private readonly IMemoryCache _cache;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(90); // > frontend's 60s poll interval

        public WhaleIntelService(IMemoryCache cache)
        {
            _cache = cache;
        }

        public async Task<List<Candle1>> GetCandlesCachedAsync(string symbol, string interval, string range)
        {
            var key = $"candles:{symbol}:{interval}:{range}";
            if (_cache.TryGetValue(key, out List<Candle1>? cached) && cached != null)
                return cached;

            var candles = await YahooClient.GetCandlesAsync(symbol, interval, range);
            _cache.Set(key, candles, CacheTtl);
            return candles;
        }

        /// <summary>
        /// Live "right now" read for a single symbol: one composite anomaly/accumulation score plus
        /// a breakout-confirmation gate, computed using only data through the most recent bar (never
        /// future data -- see AccumulationScoring.Compute). Returns exactly one signal representing
        /// today's state, not a growing history of past pattern matches.
        /// </summary>
        public async Task<WhaleSignal> AnalyzeSymbolLiveAsync(
            string symbol, string range, IReadOnlyList<Candle1> marketIndexCandles, AccumulationWeights weights)
        {
            var dailyCandles = await GetCandlesCachedAsync(symbol, "1d", range);

            if (dailyCandles.Count == 0)
            {
                return new WhaleSignal
                {
                    Symbol = symbol,
                    Interval = "1d",
                    Time = DateTime.UtcNow,
                    Action = "Hata",
                    Reason = "Veri alınamadı",
                    DataSufficient = false
                };
            }

            int index = dailyCandles.Count - 1;
            var c = dailyCandles[index];
            var comp = AccumulationScoring.Compute(dailyCandles, index, weights);

            if (!comp.DataSufficient)
            {
                return new WhaleSignal
                {
                    Symbol = symbol,
                    Interval = "1d",
                    Time = c.Time,
                    Value = c.Close.ToString("F2"),
                    Action = "Bekle",
                    Reason = "Yetersiz geçmiş veri (analiz için en az " + weights.MinHistoryBars + " günlük mum gerekli)",
                    DataSufficient = false
                };
            }

            var dailyCloses = new List<double>(dailyCandles.Count);
            for (int i = 0; i < dailyCandles.Count; i++) dailyCloses.Add(dailyCandles[i].Close);

            string action = "Bekle";

            // Breakout check: today's close vs the high/low of the trailing compression window,
            // EXCLUDING today.
            bool isBreakoutUp = false, isBreakoutDown = false;
            if (index >= weights.CompressionWindow)
            {
                double rangeHigh = double.MinValue, rangeLow = double.MaxValue;
                for (int i = index - weights.CompressionWindow; i < index; i++)
                {
                    rangeHigh = Math.Max(rangeHigh, dailyCandles[i].High);
                    rangeLow = Math.Min(rangeLow, dailyCandles[i].Low);
                }
                isBreakoutUp = c.Close > rangeHigh;
                isBreakoutDown = c.Close < rangeLow;
            }

            if (comp.Score >= weights.CompositeThresholdBreakout && (isBreakoutUp || isBreakoutDown))
            {
                double dailyEma10 = EMA(dailyCloses, 10, index);
                double dailyEma50 = EMA(dailyCloses, 50, index);
                var (macd, signalLine) = MACD(dailyCloses, index);
                double rsi = RSI(dailyCloses, 14, index);

                double? marketEma50 = null;
                double? marketClose = null;
                if (marketIndexCandles.Count > 50)
                {
                    var marketCloses = marketIndexCandles.Select(x => x.Close).ToList();
                    int marketIdx = marketCloses.Count - 1;
                    marketEma50 = EMA(marketCloses, 50, marketIdx);
                    marketClose = marketCloses[marketIdx];
                }

                bool TrendOk(bool up) =>
                    !double.IsNaN(dailyEma10) && !double.IsNaN(dailyEma50) && !double.IsNaN(macd) && !double.IsNaN(rsi) &&
                    (up ? dailyEma10 > dailyEma50 && macd > signalLine && rsi < 70
                        : dailyEma10 < dailyEma50 && macd < signalLine && rsi > 30) &&
                    (marketEma50 == null || double.IsNaN(marketEma50.Value) ||
                        (up ? marketClose >= marketEma50 : marketClose <= marketEma50));

                if (isBreakoutUp && TrendOk(true)) action = "Alış";
                else if (isBreakoutDown && TrendOk(false)) action = "Satış";
            }

            if (action == "Bekle" && comp.Score >= weights.CompositeThresholdHigh)
            {
                if (comp.Cmf > weights.CmfSignThreshold) action = "Toplama";
                else if (comp.Cmf < -weights.CmfSignThreshold) action = "Dağıtım";
            }

            var patternTags = DetectPatternTags(dailyCandles, index);
            string reason = BuildReason(action, comp, patternTags);

            return new WhaleSignal
            {
                Symbol = symbol,
                Interval = "1d",
                Time = c.Time,
                Value = c.Close.ToString("F2"),
                Action = action,
                Reason = reason,
                Score = Math.Round(comp.Score, 2),
                Confidence = Math.Round(comp.Confidence, 2),
                DataSufficient = true,
                VolumeZ = Math.Round(comp.VolumeZ, 3),
                CompressionPercentile = Math.Round(comp.CompressionPercentile, 1),
                Cmf = Math.Round(comp.Cmf, 4),
                CmfSlope = Math.Round(comp.CmfSlope, 5),
                VwapDeviationPct = Math.Round(comp.VwapDeviationPct * 100, 2)
            };
        }

        /// <summary>
        /// Offline strategy validation: walks historical bars using the SAME AccumulationScoring.Compute
        /// used live, then -- and only here -- looks `forwardDays` ahead to grade already-fired signals
        /// against an ATR stop-loss/take-profit band. Never influences what fires; only grades it after
        /// the fact. Intentionally simpler than the live path (no cross-symbol market-index trend filter,
        /// since date-aligning an independently-fetched index series across arbitrary historical bars is
        /// out of scope for this offline tool).
        /// </summary>
        public async Task<List<BacktestResult>> BacktestSymbolAsync(
            string symbol, string range, int forwardDays, AccumulationWeights weights)
        {
            var dailyCandles = await GetCandlesCachedAsync(symbol, "1d", range);
            var results = new List<BacktestResult>();
            if (dailyCandles.Count < weights.MinHistoryBars + forwardDays + 1) return results;

            var dailyCloses = new List<double>(dailyCandles.Count);
            for (int i = 0; i < dailyCandles.Count; i++) dailyCloses.Add(dailyCandles[i].Close);

            for (int i = weights.MinHistoryBars; i < dailyCandles.Count - forwardDays; i++)
            {
                var comp = AccumulationScoring.Compute(dailyCandles, i, weights);
                if (!comp.DataSufficient || comp.Score < weights.CompositeThresholdBreakout) continue;

                var c = dailyCandles[i];
                string action = "Bekle";

                bool isBreakoutUp = false, isBreakoutDown = false;
                if (i >= weights.CompressionWindow)
                {
                    double rangeHigh = double.MinValue, rangeLow = double.MaxValue;
                    for (int k = i - weights.CompressionWindow; k < i; k++)
                    {
                        rangeHigh = Math.Max(rangeHigh, dailyCandles[k].High);
                        rangeLow = Math.Min(rangeLow, dailyCandles[k].Low);
                    }
                    isBreakoutUp = c.Close > rangeHigh;
                    isBreakoutDown = c.Close < rangeLow;
                }

                double dailyEma10 = EMA(dailyCloses, 10, i);
                double dailyEma50 = EMA(dailyCloses, 50, i);
                var (macd, signalLine) = MACD(dailyCloses, i);
                double rsi = RSI(dailyCloses, 14, i);
                bool TrendOk(bool up) =>
                    !double.IsNaN(dailyEma10) && !double.IsNaN(dailyEma50) && !double.IsNaN(macd) && !double.IsNaN(rsi) &&
                    (up ? dailyEma10 > dailyEma50 && macd > signalLine && rsi < 70
                        : dailyEma10 < dailyEma50 && macd < signalLine && rsi > 30);

                if (isBreakoutUp && TrendOk(true)) action = "Alış";
                else if (isBreakoutDown && TrendOk(false)) action = "Satış";
                else if (comp.Score >= weights.CompositeThresholdHigh)
                {
                    if (comp.Cmf > weights.CmfSignThreshold) action = "Toplama";
                    else if (comp.Cmf < -weights.CmfSignThreshold) action = "Dağıtım";
                }

                if (action == "Bekle") continue; // only grade bars where something actually fired

                double entry = c.Close;
                double future = dailyCandles[i + forwardDays].Close;
                double atr = ATR(dailyCandles, 14, i);
                if (double.IsNaN(atr)) continue;

                bool isBuyLike = action == "Alış" || action == "Toplama";
                double stopLoss = isBuyLike ? entry - atr * 2 : entry + atr * 2;
                double takeProfit = isBuyLike ? entry + atr * 3 : entry - atr * 3;

                double score;
                bool hitTp, hitSl;
                if (isBuyLike)
                {
                    hitTp = future >= takeProfit;
                    hitSl = future <= stopLoss;
                    score = hitTp ? 100 : hitSl ? 0 : Math.Clamp((future - stopLoss) / (takeProfit - stopLoss) * 100, 0, 100);
                }
                else
                {
                    hitTp = future <= takeProfit;
                    hitSl = future >= stopLoss;
                    score = hitTp ? 100 : hitSl ? 0 : Math.Clamp((stopLoss - future) / (stopLoss - takeProfit) * 100, 0, 100);
                }

                results.Add(new BacktestResult
                {
                    Symbol = symbol,
                    SignalTime = c.Time,
                    Action = action,
                    EntryPrice = Math.Round(entry, 2),
                    ForwardReturnPct = Math.Round((future - entry) / entry * 100, 2),
                    HitTakeProfit = hitTp,
                    HitStopLoss = hitSl,
                    BacktestScore = Math.Round(score, 2)
                });
            }

            return results;
        }

        // --- Candlestick pattern tags: informational only, never gate Action/Score ---
        private static List<string> DetectPatternTags(List<Candle1> candles, int i)
        {
            var tags = new List<string>();
            var c = candles[i];

            if (i > 0)
            {
                var prev = candles[i - 1];
                if (c.Close > c.Open && prev.Close < prev.Open && c.Low < prev.Low && c.High > prev.High)
                    tags.Add("Yutan Boğa Deseni");
                else if (c.Close < c.Open && prev.Close > prev.Open && c.Low < prev.Low && c.High > prev.High)
                    tags.Add("Yutan Ayı Deseni");

                double body = Math.Abs(c.Close - c.Open);
                double lowerShadow = Math.Min(c.Open, c.Close) - c.Low;
                double upperShadow = c.High - Math.Max(c.Open, c.Close);
                if (lowerShadow > body * 2 && upperShadow < body * 0.5)
                {
                    if (prev.Close > c.Close) tags.Add("Çekiç Deseni");
                    else if (prev.Close < c.Close) tags.Add("Asılı Adam Deseni");
                }
            }

            if (i > 2)
            {
                var c1 = candles[i - 2]; var c2 = candles[i - 1]; var c3 = c;
                if (c1.Close > c1.Open && c2.Close > c2.Open && c3.Close > c3.Open && c2.Close > c1.Close && c3.Close > c2.Close)
                    tags.Add("Üç Beyaz Asker Deseni");
                else if (c1.Close < c1.Open && c2.Close < c2.Open && c3.Close < c3.Open && c2.Close < c1.Close && c3.Close < c2.Close)
                    tags.Add("Üç Siyah Karga Deseni");
            }

            return tags;
        }

        private static string BuildReason(string action, AccumulationComponents comp, List<string> patternTags)
        {
            var parts = new List<string>();
            switch (action)
            {
                case "Toplama": parts.Add($"Toplama sinyali (CMF={comp.Cmf:F3})"); break;
                case "Dağıtım": parts.Add($"Dağıtım sinyali (CMF={comp.Cmf:F3})"); break;
                case "Alış": parts.Add("Sıkışma sonrası yukarı kırılım + trend onayı"); break;
                case "Satış": parts.Add("Sıkışma sonrası aşağı kırılım + trend onayı"); break;
                default: parts.Add("Belirgin anomali yok"); break;
            }
            if (patternTags.Count > 0) parts.Add(string.Join(" + ", patternTags));
            return string.Join(" + ", parts);
        }

        // --- Shared TA helpers (unchanged math, reused by both live and backtest paths) ---
        public static double ATR(List<Candle1> candles, int period, int index)
        {
            if (index < period) return double.NaN;
            double trSum = 0;
            for (int i = index - period + 1; i <= index; i++)
            {
                if (i <= 0) continue;
                trSum += Indicators.TrueRange(candles[i], candles[i - 1]);
            }
            return trSum / period;
        }

        public static double EMA(List<double> prices, int period, int index)
        {
            if (index < period - 1 || index >= prices.Count) return double.NaN;
            double ema = prices.Skip(index - period + 1).Take(period).Average();
            double multiplier = 2.0 / (period + 1);
            for (int i = index - period + 1; i <= index; i++)
                ema = (prices[i] - ema) * multiplier + ema;
            return ema;
        }

        public static (double macd, double signal) MACD(List<double> closes, int index, int fast = 12, int slow = 26, int signalPeriod = 9)
        {
            double emaFast = EMA(closes, fast, index);
            double emaSlow = EMA(closes, slow, index);
            if (double.IsNaN(emaFast) || double.IsNaN(emaSlow)) return (double.NaN, double.NaN);

            double macd = emaFast - emaSlow;
            var macdList = new List<double>();
            int startIndex = Math.Max(0, index - signalPeriod);
            for (int i = startIndex; i <= index; i++)
            {
                double ef = EMA(closes, fast, i);
                double es = EMA(closes, slow, i);
                if (!double.IsNaN(ef) && !double.IsNaN(es)) macdList.Add(ef - es);
            }
            double signal = macdList.Any() ? macdList.Average() : double.NaN;
            return (macd, signal);
        }

        public static double RSI(List<double> closes, int period, int index)
        {
            if (index < period) return double.NaN;
            double gain = 0, loss = 0;
            for (int i = index - period + 1; i <= index; i++)
            {
                if (i <= 0) continue;
                double change = closes[i] - closes[i - 1];
                if (change > 0) gain += change; else loss -= change;
            }
            if (loss == 0) return 100;
            double rs = gain / loss;
            return 100 - (100 / (1 + rs));
        }
    }
}
