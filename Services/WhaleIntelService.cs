using CoreApp.Models;
using CoreApp.Services;

public class WhaleIntelService
{
    /// <summary>
    /// Manipülasyon analizi – tüm mumlar için
    /// </summary>
    public async Task<List<WhaleSignal>> AnalyzeDailySymbolSignalsAsync(string symbol, string interval, string range)
    {
        // 1 yıllık günlük mum verisi çek
        var candles = await YahooClient.GetCandlesAsync(symbol, interval, range);
        if (candles.Count < 20) return new List<WhaleSignal>();

        int period = 20;
        double multiplier = 2.3; // ProjeAdam mantığı

        var signals = new List<WhaleSignal>();

        for (int i = period; i < candles.Count; i++)
        {
            var current = candles[i];
            var past = candles.Skip(i - period).Take(period).ToList();

            // Günlük volatilite
            double todayVol = (current.High - current.Low) / current.Close;

            // Ortalama ve standart sapma
            var vols = past.Select(c => (c.High - c.Low) / c.Close).ToList();
            double avg = vols.Average();
            double std = Math.Sqrt(vols.Average(v => Math.Pow(v - avg, 2)));

            double upper = avg + std * multiplier;
            double lower = avg - std * multiplier;

            string action;
            string reason;
            double score;

            if (todayVol > upper)
            {
                action = "Manipülasyon";
                reason = "Volatilite üst bant dışında";
                score = 1.0;
            }
            else if (todayVol < lower)
            {
                action = "Sakin";
                reason = "Volatilite alt bant dışında";
                score = 0.2;
            }
            else
            {
                action = "Normal";
                reason = "Volatilite kanal içinde";
                score = 0.5;
            }

            if (score <= 0.5)
                continue;

            signals.Add(new WhaleSignal
            {
                Symbol = symbol,
                Interval = "1d",
                Open = candles[i].Open.ToString("F2"),
                Value = candles[i].Close.ToString("F2"),
                Time = current.Time,
                Action = action,
                Reason = reason,
                Score = score,
                Confidence = 0.8
            });
        }

        return signals;
    }
}
