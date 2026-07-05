using CoreApp.Models;
using System;
using System.Linq;
using System.Collections.Generic;

namespace CoreApp.Services
{
    public static class Indicators
    {
        public static double SMA(IReadOnlyList<double> v, int endExclusive, int length)
        {
            if (endExclusive < length) return double.NaN;
            double sum = 0;
            for (int i = endExclusive - length; i < endExclusive; i++) sum += v[i];
            return sum / length;
        }

        public static double Std(IReadOnlyList<double> v, int endExclusive, int length)
        {
            var m = SMA(v, endExclusive, length);
            if (double.IsNaN(m)) return double.NaN;
            double s = 0;
            for (int i = endExclusive - length; i < endExclusive; i++)
            {
                var d = v[i] - m; s += d * d;
            }
            return Math.Sqrt(s / length);
        }

        public static double TrueRange(Candle1 cur, Candle1 prev)
        {
            return Math.Max(cur.High - cur.Low,
                     Math.Max(Math.Abs(cur.High - prev.Close),
                              Math.Abs(cur.Low - prev.Close)));
        }

        public static double ATR(IReadOnlyList<Candle1> c, int endExclusive, int length)
        {
            if (endExclusive < length + 1) return double.NaN;
            double sum = 0;
            for (int i = endExclusive - length; i < endExclusive; i++)
            {
                sum += TrueRange(c[i], c[i - 1]);
            }
            return sum / length;
        }

        public static double EWMA(IReadOnlyList<double> series, int endIdx, int period)
        {
            if (endIdx < 0) return double.NaN;
            int start = Math.Max(0, endIdx - period + 1);
            double k = 2.0 / (period + 1.0);
            double ema = series[start];
            for (int i = start + 1; i <= endIdx; i++)
                ema = series[i] * k + ema * (1 - k);
            return ema;
        }

        public static double MAD(IReadOnlyList<double> v, int endExclusive, int length)
        {
            if (endExclusive < length) return double.NaN;
            var slice = new double[length];
            for (int i = 0; i < length; i++) slice[i] = v[endExclusive - length + i];
            var med = Median(slice);
            var abs = slice.Select(x => Math.Abs(x - med)).ToArray();
            return Median(abs);
        }

        private static double Median(double[] a)
        {
            Array.Sort(a);
            int n = a.Length;
            if (n % 2 == 1) return a[n / 2];
            return 0.5 * (a[n / 2 - 1] + a[n / 2]);
        }

        public static double ZScore(IReadOnlyList<double> v, int endExclusive, int length)
        {
            var m = SMA(v, endExclusive, length);
            var s = Std(v, endExclusive, length);
            if (double.IsNaN(m) || s == 0) return double.NaN;
            return (v[endExclusive - 1] - m) / s;
        }

        // endExclusive is a true exclusive upper bound (consistent with SMA/Std/MAD): the window
        // is candles[endExclusive-window .. endExclusive-1].
        public static double OBVSlope(IReadOnlyList<Candle1> candles, int endExclusive, int window)
        {
            if (endExclusive < window) return 0;
            int start = endExclusive - window;
            double val = 0, obvFirst = 0, obvLast = 0;
            for (int i = 0; i < window; i++)
            {
                int idx = start + i;
                if (idx > 0)
                {
                    if (candles[idx].Close > candles[idx - 1].Close) val += candles[idx].Volume;
                    else if (candles[idx].Close < candles[idx - 1].Close) val -= candles[idx].Volume;
                }
                if (i == 0) obvFirst = val;
                obvLast = val;
            }
            return (obvLast - obvFirst) / Math.Max(1.0, window);
        }

        public static double ADX(IReadOnlyList<Candle1> candles, int endExclusive, int period = 14)
        {
            int start = endExclusive - period - 1;
            if (start < 0) return double.NaN;
            var tr = new double[period];
            var plusDM = new double[period];
            var minusDM = new double[period];

            for (int i = 1; i <= period; i++)
            {
                int idx = start + i;
                var cur = candles[idx];
                var prev = candles[idx - 1];
                double highDiff = cur.High - prev.High;
                double lowDiff = prev.Low - cur.Low;

                plusDM[i - 1] = (highDiff > lowDiff && highDiff > 0) ? highDiff : 0;
                minusDM[i - 1] = (lowDiff > highDiff && lowDiff > 0) ? lowDiff : 0;
                tr[i - 1] = Math.Max(cur.High - cur.Low, Math.Max(Math.Abs(cur.High - prev.Close), Math.Abs(cur.Low - prev.Close)));
            }

            double atr = tr.Average();
            if (atr == 0) return double.NaN;
            double plus = plusDM.Sum() / atr;
            double minus = minusDM.Sum() / atr;
            double dx = Math.Abs(plus - minus) / (plus + minus + 1e-9);
            return dx; // 0..1 approx
        }

        // endExclusive is a true exclusive upper bound here (consistent with SMA/Std/MAD):
        // the window is candles[endExclusive-lookback .. endExclusive-1].
        public static double VWAP(IReadOnlyList<Candle1> candles, int endExclusive, int lookback)
        {
            int start = Math.Max(0, endExclusive - lookback);
            double pv = 0;
            double volSum = 0;
            for (int i = start; i < endExclusive; i++)
            {
                double typical = (candles[i].High + candles[i].Low + candles[i].Close) / 3.0;
                pv += typical * candles[i].Volume;
                volSum += candles[i].Volume;
            }
            return volSum == 0 ? double.NaN : pv / volSum;
        }

        // (Close[index] - VWAP) / VWAP, using the trailing `lookback` bars up to and including index.
        public static double VWAPDeviation(IReadOnlyList<Candle1> candles, int index, int lookback)
        {
            double vwap = VWAP(candles, index + 1, lookback);
            if (double.IsNaN(vwap) || vwap == 0) return double.NaN;
            return (candles[index].Close - vwap) / vwap;
        }

        public static double MedianOfWindow(IReadOnlyList<double> v, int endExclusive, int length)
        {
            if (endExclusive < length) return double.NaN;
            var slice = new double[length];
            for (int i = 0; i < length; i++) slice[i] = v[endExclusive - length + i];
            return Median(slice);
        }

        // MAD-based robust z-score of the most recent value vs. its own trailing window.
        // Falls back to a plain Std-based denominator if MAD is ~0 (e.g. several identical
        // readings in thin/illiquid names), and returns NaN only if that's also ~0 (flat series).
        public static double RobustZScore(IReadOnlyList<double> v, int endExclusive, int length)
        {
            if (endExclusive < length) return double.NaN;
            var med = MedianOfWindow(v, endExclusive, length);
            if (double.IsNaN(med)) return double.NaN;

            double denom = MAD(v, endExclusive, length) * 1.4826; // normal-consistency scaling
            if (denom < 1e-9) denom = Std(v, endExclusive, length);
            if (double.IsNaN(denom) || denom < 1e-9) return double.NaN;

            return (v[endExclusive - 1] - med) / denom;
        }

        // Percentile rank (0-100) of v[endExclusive-1] against the `length` values immediately
        // preceding it. History EXCLUDES the value being ranked. NaN if insufficient history.
        public static double PercentileRank(IReadOnlyList<double> v, int endExclusive, int length)
        {
            int histEnd = endExclusive - 1;
            if (histEnd - length < 0) return double.NaN;
            double current = v[endExclusive - 1];
            int countBelowOrEqual = 0;
            for (int i = histEnd - length; i < histEnd; i++)
            {
                if (v[i] <= current) countBelowOrEqual++;
            }
            return 100.0 * countBelowOrEqual / length;
        }

        // Chaikin Money Flow over a rolling window: sum(CLV*Volume) / sum(Volume).
        // Candle1.Range already floors at 1e-9, so limit-up/limit-down bars (High==Low==Close)
        // correctly contribute CLV=0 rather than NaN/Infinity.
        public static double CMF(IReadOnlyList<Candle1> candles, int endExclusive, int length)
        {
            if (endExclusive < length) return double.NaN;
            double mfvSum = 0, volSum = 0;
            for (int i = endExclusive - length; i < endExclusive; i++)
            {
                var c = candles[i];
                double clv = ((c.Close - c.Low) - (c.High - c.Close)) / c.Range;
                mfvSum += clv * c.Volume;
                volSum += c.Volume;
            }
            return volSum <= 0 ? 0.0 : mfvSum / volSum;
        }

        public static double CMFSlope(IReadOnlyList<Candle1> candles, int endExclusive, int cmfLength, int slopeWindow)
        {
            if (endExclusive - slopeWindow < cmfLength) return double.NaN;
            double now = CMF(candles, endExclusive, cmfLength);
            double past = CMF(candles, endExclusive - slopeWindow, cmfLength);
            if (double.IsNaN(now) || double.IsNaN(past)) return double.NaN;
            return (now - past) / Math.Max(1, slopeWindow);
        }

        // Squashes an unbounded/z-like value into 0-100, centered at 50 = neutral.
        public static double Logistic0to100(double z, double k = 1.0)
        {
            if (double.IsNaN(z)) return 50.0;
            return 100.0 / (1.0 + Math.Exp(-k * z));
        }
    }
}
