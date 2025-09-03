using CoreApp.Models;

namespace CoreApp.Services
{
    public class WhaleIntelService
    {
        /// <summary>
        /// Günlük mumları analiz eder, sinyalleri üretir ve her sinyal için backtest yapar.
        /// </summary>
        /// <param name="symbol">Sembol (örn: ASELS.IS)</param>
        /// <param name="backtestDays">Backtest kaç gün sonrası yapılacak (1,3,5,10)</param>
        public async Task<List<WhaleSignal>> AnalyzeDailySymbolAsSignalsAsync(string symbol,string interval,string range, int backtestDays = 3)
        {
            var candles = await YahooClient.GetCandlesAsync(symbol, interval, range);
            var closes = candles.Select(c => c.Close).ToList();
            var signals = new List<WhaleSignal>();

            for (int i = 0; i < candles.Count; i++)
            {
                var c = candles[i];
                string? action = null;
                string? reason = null;

                // Ortalama hacim (20 gün)
                var avgVol = candles.Skip(Math.Max(0, i - 20)).Take(20).Average(x => x.Volume);

                // --- Manipülasyon sinyali ---
                if (c.Close > c.Open && c.Volume > avgVol * 1.5) action = "Al";
                else if (c.Close < c.Open && c.Volume > avgVol * 1.5) action = "Sat";

                // --- Kaufman Kama ---
                var kamaRange = candles.Skip(Math.Max(0, i - 10)).Take(10).ToList();
                if (kamaRange.Count == 10)
                {
                    var maxHigh = kamaRange.Max(x => x.High);
                    var minLow = kamaRange.Min(x => x.Low);
                    var kamaWidth = (maxHigh - minLow) / minLow * 100;

                    if (kamaWidth < 3 && c.Close > maxHigh) { action = "Al"; reason = "Kaufman Kama yukarı kırılımı"; }
                    else if (kamaWidth < 3 && c.Close < minLow) { action = "Sat"; reason = "Kaufman Kama aşağı kırılımı"; }
                }

                // --- Trend ve Momentum kontrolü ---
                double ema10 = EMA(closes, 10, i);
                double ema50 = EMA(closes, 50, i);
                var (macd, signalLine) = MACD(closes, i);
                double rsi = RSI(closes, 14, i);

                if (action != null)
                {
                    // EMA + MACD + RSI ile doğrulama
                    if (action == "Al" && ema10 > ema50 && macd > signalLine && rsi < 70)
                        reason = (reason ?? "") + " + Trend/Momentum onayı";
                    else if (action == "Sat" && ema10 < ema50 && macd < signalLine && rsi > 30)
                        reason = (reason ?? "") + " + Trend/Momentum onayı";

                    var signal = new WhaleSignal
                    {
                        Symbol = symbol,
                        Interval = "1d",
                        Time = c.Time,
                        Action = action,
                        Reason = reason ?? "",
                        Score = 0,
                        Confidence = 0
                    };

                    // --- Backtest ---
                    if (i + backtestDays < candles.Count)
                    {
                        var entry = c.Close;
                        var future = candles[i + backtestDays].Close;
                        var change = (future - entry) / entry * 100;

                        double score = 0;
                        if (action == "Al") score = change > 0 ? Math.Min(100, change * 20) : Math.Max(0, 100 + change * 20);
                        else score = change < 0 ? Math.Min(100, Math.Abs(change) * 20) : Math.Max(0, 100 - change * 20);

                        signal.Score = Math.Round(score, 2);
                        signal.Confidence = Math.Round(score, 2);
                    }

                    signals.Add(signal);
                }
            }

            return signals;
        }

        // -------------------
        // EMA Hesaplama
        public static double EMA(List<double> prices, int period, int index)
        {
            if (index < period - 1) return double.NaN;

            double multiplier = 2.0 / (period + 1);
            double ema = prices.GetRange(index - period + 1, period).Average();

            for (int i = index - period + 1; i <= index; i++)
            {
                ema = ((prices[i] - ema) * multiplier) + ema;
            }
            return ema;
        }

        // -------------------
        // MACD Hesaplama
        public static (double macd, double signal) MACD(List<double> closes, int index, int fast = 12, int slow = 26, int signalPeriod = 9)
        {
            double emaFast = EMA(closes, fast, index);
            double emaSlow = EMA(closes, slow, index);

            if (double.IsNaN(emaFast) || double.IsNaN(emaSlow))
                return (double.NaN, double.NaN);

            double macd = emaFast - emaSlow;

            List<double> macdList = new List<double>();
            for (int i = Math.Max(0, index - signalPeriod + 1); i <= index; i++)
            {
                double ef = EMA(closes, fast, i);
                double es = EMA(closes, slow, i);
                if (!double.IsNaN(ef) && !double.IsNaN(es))
                    macdList.Add(ef - es);
            }
            double signal = macdList.Count > 0 ? macdList.Average() : double.NaN;

            return (macd, signal);
        }

        // -------------------
        // RSI Hesaplama
        public static double RSI(List<double> closes, int period, int index)
        {
            if (index < period) return double.NaN;

            double gain = 0, loss = 0;
            for (int i = index - period + 1; i <= index; i++)
            {
                double change = closes[i] - closes[i - 1];
                if (change > 0) gain += change;
                else loss -= change;
            }

            if (loss == 0) return 100;
            double rs = gain / loss;
            return 100 - (100 / (1 + rs));
        }
    }
}
