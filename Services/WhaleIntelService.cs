using CoreApp.Models;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CoreApp.Services
{
    public class WhaleIntelService
    {
        private readonly IMemoryCache _cache;
        private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
        private readonly object _cacheLock = new();

        public class Settings
        {
            public int MinCandles { get; set; } = 60;
            public int LookbackVolume { get; set; } = 20;
            public int LookbackReturns { get; set; } = 50;
            public int TopN { get; set; } = 5;
            public int CooldownSeconds { get; set; } = 120;
            public int ImportantThreshold { get; set; } = 70;
            public bool MultiTimeframeConfirmation { get; set; } = true;
            public string ConfirmationInterval { get; set; } = "5m";
            public string ConfirmationRange { get; set; } = "1d";
        }

        private readonly Settings _cfg;

        public WhaleIntelService(IMemoryCache cache, Settings? settings = null)
        {
            _cache = cache;
            _cfg = settings ?? new Settings();
        }

        private async Task<List<Candle>> GetCandlesCached(string symbol, string interval, string range)
        {
            var key = $"yahoo:{symbol}:{interval}:{range}";
            if (!_cache.TryGetValue(key, out List<Candle>? candles))
            {
                // prevent dogpile: lock per key (simple)
                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(key, out candles)) return candles!;
                }

                candles = await YahooClient.GetCandlesAsync(symbol, interval, range);
                _cache.Set(key, candles, TimeSpan.FromSeconds(10));
            }
            return candles!;
        }

        /// <summary>
        /// Public: top important sinyalleri döner (async + cancellation)
        /// </summary>
        public async Task<List<WhaleSignal>> GetTopImportantSignalsAsync(string symbol, string interval, string range, CancellationToken ct = default)
        {
            var all = await GetAllSignalsAsync(symbol, interval, range, ct);

            var now = DateTime.UtcNow;
            var allowed = new List<WhaleSignal>();
            foreach (var s in all)
            {
                if (s.Score < _cfg.ImportantThreshold) continue;

                // cooldown (atomic)
                var key = $"{s.Symbol}:{s.Action}";
                var until = _cooldowns.GetOrAdd(key, DateTime.MinValue);
                if (until > now) continue;

                _cooldowns[key] = now.AddSeconds(_cfg.CooldownSeconds);
                allowed.Add(s);
            }

            return allowed
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Confidence)
                .Take(_cfg.TopN)
                .ToList();
        }

        /// <summary>
        /// Produces all candidate signals with composite scores (internal)
        /// </summary>
        private async Task<List<WhaleSignal>> GetAllSignalsAsync(string symbol, string interval, string range, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var candles = await GetCandlesCached(symbol, interval, range);
            if (candles == null || candles.Count < _cfg.MinCandles) return new List<WhaleSignal>();

            int n = candles.Count;
            var vols = new double[n];
            var returns = new double[n];
            var closes = new double[n];
            var opens = new double[n];
            var highs = new double[n];
            var lows = new double[n];

            for (int i = 0; i < n; i++)
            {
                vols[i] = candles[i].Volume;
                closes[i] = (double)candles[i].Close;
                opens[i] = (double)candles[i].Open;
                highs[i] = (double)candles[i].High;
                lows[i] = (double)candles[i].Low;
                if (i > 0) returns[i] = (closes[i] - closes[i - 1]) / Math.Max(1.0, closes[i - 1]);
            }

            var results = new List<WhaleSignal>(capacity: 32);

            // precompute rolling statistics on volumes to avoid repeated work
            for (int i = 1; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();

                // compute basic window-based metrics
                if (i < _cfg.LookbackVolume) continue; // need enough history
                double vAvg = Indicators.SMA(vols, i, _cfg.LookbackVolume);
                if (double.IsNaN(vAvg) || vAvg <= 0) continue;
                double vStd = Indicators.Std(vols, i, _cfg.LookbackVolume);
                double vMad = Indicators.MAD(vols, i, _cfg.LookbackVolume);
                double rvol = vols[i] / vAvg;
                double zVol = (!double.IsNaN(vStd) && vStd > 0) ? (vols[i] - vAvg) / vStd : 0;

                var c = candles[i];
                double bodyRatio = c.Body / c.Range;

                // base signals (compact)
                WhaleSignal? baseSig = null;

                if (rvol >= 2.0 && bodyRatio <= 0.25)
                {
                    double last5 = 0;
                    if (i >= 5) last5 = returns.Skip(i - 5).Take(5).Sum();
                    var action = last5 < 0 ? "Toplama" : "Dağıtım";
                    baseSig = MakeSignal(symbol, interval, c, action, $"RVOL≈{rvol:0.00}; bodyRatio={bodyRatio:0.00}; last5={last5:0.000}");
                }
                else
                {
                    // momentum rules
                    double rStd = Indicators.Std(returns, i, _cfg.LookbackReturns);
                    if (!double.IsNaN(rStd) && rStd > 0)
                    {
                        if (returns[i] >= 2.0 * rStd && rvol >= 2.0)
                            baseSig = MakeSignal(symbol, interval, c, "Alış", $"MomentumUp; RVOL≈{rvol:0.00}");
                        else if (returns[i] <= -2.0 * rStd && rvol >= 2.0)
                            baseSig = MakeSignal(symbol, interval, c, "Satış", $"MomentumDown; RVOL≈{rvol:0.00}");
                    }
                }

                // climax
                if (baseSig == null && bodyRatio >= 0.7 && rvol >= 1.5)
                {
                    var action = c.IsUp ? "Satış" : "Alış";
                    baseSig = MakeSignal(symbol, interval, c, action, $"CLIMAX; RVOL≈{rvol:0.00}");
                }

                // predictive hint always computed (cheap)
                var hint = ComputeCompactPrediction(candles, i, rvol, closes, vols);

                if (baseSig != null)
                {
                    ComposeScore(baseSig, candles, i, rvol, zVol, vMad, hint);
                    if (_cfg.MultiTimeframeConfirmation)
                    {
                        var confirmed = await TryMultiTimeframeConfirmAsync(symbol, baseSig.Time, ct);
                        if (confirmed)
                        {
                            baseSig.Score = Math.Min(100, baseSig.Score + 10);
                            baseSig.Confidence = Math.Min(1.0, baseSig.Confidence + 0.1);
                            baseSig.Confirmations++;
                        }
                    }
                    baseSig.SuggestedAction = SuggestFromScore(baseSig.Score, baseSig.Confidence);
                    results.Add(baseSig);
                }
                else if (hint != null && hint.Score >= _cfg.ImportantThreshold)
                {
                    hint.SuggestedAction = SuggestFromScore(hint.Score, hint.Confidence);
                    results.Add(hint);
                }
            }

            return results;
        }

        // compact predictive hint (lightweight)
        private WhaleSignal? ComputeCompactPrediction(List<Candle> candles, int idx, double rvol, double[] closes, double[] vols)
        {
            if (idx < 30) return null;
            double ema20 = Indicators.EWMA(closes, idx, 20);
            double ema50 = Indicators.EWMA(closes, idx, 50);
            double momentum3 = (closes[idx] - closes[Math.Max(0, idx - 3)]) / Math.Max(1.0, closes[Math.Max(0, idx - 3)]);
            double rsi14 = RSI(closes, idx, 14);
            double std20 = Indicators.Std(closes, idx, 20);
            double sma20 = Indicators.SMA(closes, idx, 20);
            double bbWidth = (sma20 + 2 * std20 - (sma20 - 2 * std20)) / Math.Max(1.0, sma20);
            double obvSlope = Indicators.OBVSlope(candles, idx, 10);

            double score = 0;
            double weight = 0;

            if (!double.IsNaN(ema20) && !double.IsNaN(ema50))
            {
                weight += 1.2;
                score += (ema20 > ema50 ? 1 : -1) * 0.8;
            }

            weight += 1.0;
            score += (rvol >= 2.0 ? (momentum3 > 0 ? 0.6 : -0.3) : 0);

            weight += 0.9;
            if (!double.IsNaN(rsi14))
            {
                if (rsi14 >= 70) score += -0.9;
                else if (rsi14 <= 30) score += 0.9;
            }

            weight += 0.6;
            if (bbWidth < 0.02) score += (momentum3 > 0 ? 0.4 : -0.4);

            weight += 0.5;
            score += (obvSlope > 0 ? 0.3 : -0.15);

            double normalized = weight > 0 ? score / weight : 0;
            double probUp = Sigmoid(normalized * 1.5);
            double expectedReturn = normalized * 0.01;
            double confidence = Math.Min(0.2 + Math.Abs(normalized) * 0.6, 0.98);
            string suggested = "Bekle";
            if (probUp >= 0.85 && confidence > 0.6) suggested = "Güçlü Al";
            else if (probUp >= 0.6) suggested = "Al";
            else if (probUp <= 0.15 && confidence > 0.6) suggested = "Güçlü Sat";
            else if (probUp <= 0.4) suggested = "Sat";

            var c = candles[idx];
            var ws = MakeSignal("", "", c, suggested, $"PredictHint prob={probUp:0.00} conf={confidence:0.00}");
            ws.PredictedUpProbability = Math.Round(probUp, 3);
            ws.ExpectedReturnPct = Math.Round(expectedReturn, 5);
            ws.Confidence = Math.Round(confidence, 3);
            ws.Score = (int)Math.Round(Math.Min(100, Math.Max(0, 50 + normalized * 50)));
            ws.PredictionHorizonBars = 5;
            ws.SuggestedAction = suggested;
            return ws;
        }

        private void ComposeScore(WhaleSignal sig, List<Candle> candles, int idx, double rvol, double zVol, double madScore, WhaleSignal? hint)
        {
            double score = 30;
            double conf = 0.4;
            int confirms = 0;

            if (rvol >= 5) { score += 30; conf += 0.2; }
            else if (rvol >= 3) { score += 20; conf += 0.12; }
            else if (rvol >= 2) { score += 10; conf += 0.06; }

            if (!double.IsNaN(zVol) && zVol >= 3) { score += 10; conf += 0.05; }

            if (hint != null)
            {
                score += Math.Min(20, hint.Score * 0.2);
                conf = Math.Max(conf, hint.Confidence);
                confirms += hint.Confirmations;
            }

            double adx = Indicators.ADX(candles, idx, 14);
            if (!double.IsNaN(adx))
            {
                if (adx >= 0.6) { score += 15; conf += 0.1; }
                else if (adx >= 0.3) { score += 7; conf += 0.04; }
            }

            double obvSlope = Indicators.OBVSlope(candles, idx, 10);
            if (Math.Abs(obvSlope) > 0)
            {
                if (sig.Action == "Alış" && obvSlope > 0) { score += 8; conf += 0.05; confirms++; }
                if (sig.Action == "Satış" && obvSlope < 0) { score += 8; conf += 0.05; confirms++; }
            }

            // normalize and clamp
            sig.Score = Math.Min(100, Math.Max(0, (int)Math.Round(score)));
            sig.Confidence = Math.Min(1.0, conf);
            sig.Confirmations = confirms;
            sig.Reason += $" | compositeScore={sig.Score}";
        }

        // multi-timeframe: a lightweight confirm using cached higher-T candles
        private async Task<bool> TryMultiTimeframeConfirmAsync(string symbol, DateTime baseTime, CancellationToken ct)
        {
            if (!_cfg.MultiTimeframeConfirmation) return false;
            try
            {
                var higher = await GetCandlesCached(symbol, _cfg.ConfirmationInterval, _cfg.ConfirmationRange);
                if (higher == null || higher.Count < 20) return false;
                var match = higher.LastOrDefault(h => h.Time <= baseTime);
                if (match == null) return false;
                int idx = higher.IndexOf(match);
                if (idx < 20) return false;
                var vols = higher.Select(c => (double)c.Volume).ToArray();
                double vAvg = Indicators.SMA(vols, idx, 20);
                if (vAvg <= 0) return false;
                double rvolHigh = vols[idx] / vAvg;
                double adxHigh = Indicators.ADX(higher, idx, 14);
                if (rvolHigh >= 2.0) return true;
                if (!double.IsNaN(adxHigh) && adxHigh >= 0.4) return true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static WhaleSignal MakeSignal(string sym, string intv, Candle c, string action, string reason)
            => new WhaleSignal
            {
                Symbol = sym,
                Interval = intv,
                Value = c.Close.ToString("F2"),
                Open = c.Open.ToString("F2"),
                Time = c.Time,
                Action = action,
                Reason = reason
            };

        private string SuggestFromScore(int score, double conf)
        {
            if (score >= 85 && conf > 0.6) return "Güçlü Al";
            if (score >= 65) return "Al";
            if (score <= 15 && conf > 0.6) return "Güçlü Sat";
            if (score <= 35) return "Sat";
            return "Bekle";
        }

        private double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

        private double RSI(double[] closes, int endIdx, int period = 14)
        {
            if (endIdx < period) return double.NaN;
            double gain = 0, loss = 0;
            for (int i = endIdx - period + 1; i <= endIdx; i++)
            {
                double diff = closes[i] - closes[i - 1];
                if (diff > 0) gain += diff;
                else loss -= diff;
            }
            if (gain + loss == 0) return 50.0;
            double rs = (gain / period) / (loss / period + 1e-9);
            return 100.0 - (100.0 / (1.0 + rs));
        }
    }
}
