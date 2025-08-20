using CoreApp.Models;

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

        public static double ATR(IReadOnlyList<Candle> c, int endExclusive, int length)
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
    }
}
