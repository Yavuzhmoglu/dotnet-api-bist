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

        public static double ATR(IReadOnlyList<Candle1> c, int endExclusive, int length)
        {
            if (endExclusive < length + 1) return double.NaN;
            double sum = 0;
            for (int i = endExclusive - length; i < endExclusive; i++)
            {
                var prevClose = c[i - 1].Close;
                var tr = Math.Max(c[i].High - c[i].Low,
                          Math.Max(Math.Abs(c[i].High - prevClose),
                                   Math.Abs(c[i].Low - prevClose)));
                sum += tr;
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

        public static double OBVSlope(IReadOnlyList<Candle1> candles, int endExclusive, int window)
        {
            if (endExclusive < window) return 0;
            var closes = candles.Select(c => (double)c.Close).ToArray();
            var vols = candles.Select(c => (double)c.Volume).ToArray();
            var obv = new double[window];
            double val = 0;
            for (int i = 0; i < window; i++)
            {
                int idx = endExclusive - window + 1 + i;
                if (idx == 0) continue;
                if (closes[idx] > closes[idx - 1]) val += vols[idx];
                else if (closes[idx] < closes[idx - 1]) val -= vols[idx];
                obv[i] = val;
            }
            return (obv[^1] - obv[0]) / Math.Max(1.0, window);
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

        public static double VWAP(IReadOnlyList<Candle1> candles, int endExclusive, int lookback)
        {
            int start = Math.Max(0, endExclusive - lookback + 1);
            double pv = 0;
            double volSum = 0;
            for (int i = start; i <= endExclusive; i++)
            {
                double typical = (candles[i].High + candles[i].Low + candles[i].Close) / 3.0;
                pv += typical * candles[i].Volume;
                volSum += candles[i].Volume;
            }
            return volSum == 0 ? double.NaN : pv / volSum;
        }
    }
}
