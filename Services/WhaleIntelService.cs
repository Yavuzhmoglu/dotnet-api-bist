using CoreApp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using YahooFinanceApi;

namespace CoreApp.Services
{
    public class WhaleIntelService
    {
        /// <summary>
        /// Günlük ve 4 saatlik mumları, piyasa trendini, mum formasyonlarını analiz ederek sinyaller üretir ve backtest yapar.
        /// </summary>
        public async Task<List<WhaleSignal>> AnalyzeDailySymbolAsSignalsAsync(string symbol, string range, int backtestDays = 3, string marketIndexSymbol = "XU100.IS")
        {
            var dailyCandles = await YahooClient.GetCandlesAsync(symbol, "1d", range);
            var hourlyCandles = await YahooClient.GetCandlesAsync(symbol, "4h", range);
            var marketIndexCandles = await YahooClient.GetCandlesAsync(marketIndexSymbol, "1d", range);

            var dailyCloses = dailyCandles.Select(c => c.Close).ToList();
            var hourlyCloses = hourlyCandles.Select(c => c.Close).ToList();
            var marketIndexCloses = marketIndexCandles.Select(c => c.Close).ToList();
            var signals = new List<WhaleSignal>();

            // Ana döngü başlamadan önce minimum veri kontrolü
            if (dailyCandles.Count < 50 || hourlyCandles.Count < 50 || marketIndexCandles.Count < 50)
            {
                return signals;
            }

            for (int i = 0; i < dailyCandles.Count; i++)
            {
                var c = dailyCandles[i];
                string? action = null;
                string? reason = null;

                // --- Günlük Manipülasyon ve Kaufman Kama Sinyalleri ---
                var avgVol = dailyCandles.Skip(Math.Max(0, i - 20)).Take(20).Average(x => x.Volume);
                if (c.Volume > avgVol * 1.5)
                {
                    if (c.Close > c.Open) action = "Al";
                    else if (c.Close < c.Open) action = "Sat";
                }

                var kamaRange = dailyCandles.Skip(Math.Max(0, i - 10)).Take(10).ToList();
                if (kamaRange.Count == 10)
                {
                    var maxHigh = kamaRange.Max(x => x.High);
                    var minLow = kamaRange.Min(x => x.Low);
                    var kamaWidth = (maxHigh - minLow) / minLow * 100;

                    if (kamaWidth < 3)
                    {
                        if (c.Close > maxHigh) { action = "Al"; reason = "Kaufman Kama yukarı kırılımı"; }
                        else if (c.Close < minLow) { action = "Sat"; reason = "Kaufman Kama aşağı kırılımı"; }
                    }
                }

                // --- Mum Formasyonu Onayı (İndeks 0 ve 1 için kontrol) ---
                if (i > 0)
                {
                    var previousCandle = dailyCandles[i - 1];

                    if (c.Close > c.Open && previousCandle.Close < previousCandle.Open && c.Low < previousCandle.Low && c.High > previousCandle.High)
                    {
                        if (action == null) action = "Al";
                        reason = (reason ?? "") + " + Yutan Boğa Deseni";
                    }
                    else if (c.Close < c.Open && previousCandle.Close > previousCandle.Open && c.Low < previousCandle.Low && c.High > previousCandle.High)
                    {
                        if (action == null) action = "Sat";
                        reason = (reason ?? "") + " + Yutan Ayı Deseni";
                    }

                    double body = Math.Abs(c.Close - c.Open);
                    double lowerShadow = Math.Min(c.Open, c.Close) - c.Low;
                    double upperShadow = c.High - Math.Max(c.Open, c.Close);

                    if (lowerShadow > (body * 2) && upperShadow < (body * 0.5))
                    {
                        if (dailyCandles[i - 1].Close > c.Close)
                        {
                            if (action == null) action = "Al";
                            reason = (reason ?? "") + " + Çekiç Deseni";
                        }
                        else if (dailyCandles[i - 1].Close < c.Close)
                        {
                            if (action == null) action = "Sat";
                            reason = (reason ?? "") + " + Asılı Adam Deseni";
                        }
                    }
                }

                if (i > 2)
                {
                    var c1 = dailyCandles[i - 2];
                    var c2 = dailyCandles[i - 1];
                    var c3 = dailyCandles[i];
                    // Üç Beyaz Asker
                    if (c1.Close > c1.Open && c2.Close > c2.Open && c3.Close > c3.Open &&
                        c2.Close > c1.Close && c3.Close > c2.Close)
                    {
                        if (action == null) action = "Al";
                        reason = (reason ?? "") + " + Üç Beyaz Asker Deseni";
                    }
                    // Üç Siyah Karga
                    else if (c1.Close < c1.Open && c2.Close < c2.Open && c3.Close < c3.Open &&
                             c2.Close < c1.Close && c3.Close < c2.Close)
                    {
                        if (action == null) action = "Sat";
                        reason = (reason ?? "") + " + Üç Siyah Karga Deseni";
                    }
                }

                // --- Sinyal Onayı ve Filtreleme (Yeterli veri kontrolü en kritik nokta) ---
                if (action != null && i >= 50)
                {
                    // 1. Piyasa Trendi Analizi
                    if (marketIndexCloses.Count > 50)
                    {
                        double marketEma50 = EMA(marketIndexCloses, 50, i);
                        if (!double.IsNaN(marketEma50) && ((action == "Al" && marketIndexCloses[i] < marketEma50) || (action == "Sat" && marketIndexCloses[i] > marketEma50)))
                        {
                            action = null;
                            reason = null;
                        }
                    }

                    // 2. Multiframe (4 saatlik) Trend Onayı
                    var relevantHourlyIndex = hourlyCandles.FindIndex(h => h.Time >= c.Time.AddHours(-24) && h.Time <= c.Time.AddHours(1));
                    if (action != null && relevantHourlyIndex != -1 && relevantHourlyIndex >= 50)
                    {
                        double hourlyEma10 = EMA(hourlyCloses, 10, relevantHourlyIndex);
                        double hourlyEma50 = EMA(hourlyCloses, 50, relevantHourlyIndex);

                        if (!double.IsNaN(hourlyEma10) && !double.IsNaN(hourlyEma50) && ((action == "Al" && hourlyEma10 <= hourlyEma50) || (action == "Sat" && hourlyEma10 >= hourlyEma50)))
                        {
                            action = null;
                            reason = null;
                        }
                    }

                    // Eğer sinyal tüm filtreleri geçerse
                    if (action != null)
                    {
                        double dailyEma10 = EMA(dailyCloses, 10, i);
                        double dailyEma50 = EMA(dailyCloses, 50, i);
                        var (macd, signalLine) = MACD(dailyCloses, i);
                        double rsi = RSI(dailyCloses, 14, i);

                        if (!double.IsNaN(dailyEma10) && !double.IsNaN(dailyEma50) && !double.IsNaN(macd) && !double.IsNaN(rsi))
                        {
                            if (action == "Al" && dailyEma10 > dailyEma50 && macd > signalLine && rsi < 70)
                                reason = (reason ?? "") + " + Trend/Momentum onayı";
                            else if (action == "Sat" && dailyEma10 < dailyEma50 && macd < signalLine && rsi > 30)
                                reason = (reason ?? "") + " + Trend/Momentum onayı";
                        }
                        else
                        {
                            action = null; reason = null; // Yeterli veri yoksa sinyali iptal et
                        }

                        if (action != null)
                        {
                            var signal = new WhaleSignal
                            {
                                Symbol = symbol,
                                Interval = "1d",
                                Time = c.Time,
                                Value = c.Close.ToString("F2"),
                                Action = action,
                                Reason = reason ?? "Z Formasyon",
                                Score = 0,
                                Confidence = 0
                            };

                            // --- Backtest ve Dinamik Stop-Loss/Take-Profit ---
                            if (i + backtestDays < dailyCandles.Count)
                            {
                                var entry = c.Close;
                                var future = dailyCandles[i + backtestDays].Close;
                                double atr = ATR(dailyCandles, 14, i);
                                if (!double.IsNaN(atr))
                                {
                                    double stopLossMultiple = 2;
                                    double takeProfitMultiple = 3;
                                    double score = 0;

                                    if (action == "Al")
                                    {
                                        double stopLoss = entry - (atr * stopLossMultiple);
                                        double takeProfit = entry + (atr * takeProfitMultiple);
                                        if (future >= takeProfit) score = 100;
                                        else if (future <= stopLoss) score = 0;
                                        else score = Math.Min(100, (future - stopLoss) / (takeProfit - stopLoss) * 100);
                                    }
                                    else
                                    {
                                        double stopLoss = entry + (atr * stopLossMultiple);
                                        double takeProfit = entry - (atr * takeProfitMultiple);
                                        if (future <= takeProfit) score = 100;
                                        else if (future >= stopLoss) score = 0;
                                        else score = Math.Min(100, (stopLoss - future) / (stopLoss - takeProfit) * 100);
                                    }
                                    signal.Score = Math.Round(score, 2);
                                    signal.Confidence = Math.Round(score, 2);
                                }
                            }
                            signals.Add(signal);
                        }
                    }
                }
            }
            return signals;
        }

        // --- Yardımcı Metotlar (Hata Kontrollü) ---
        public static double ATR(List<Candle1> candles, int period, int index)
        {
            if (index < period) return double.NaN;
            double trSum = 0;
            for (int i = index - period + 1; i <= index; i++)
            {
                if (i <= 0) continue;
                double highLow = candles[i].High - candles[i].Low;
                double highClose = Math.Abs(candles[i].High - candles[i - 1].Close);
                double lowClose = Math.Abs(candles[i].Low - candles[i - 1].Close);
                trSum += Math.Max(highLow, Math.Max(highClose, lowClose));
            }
            return trSum / period;
        }

        public static double EMA(List<double> prices, int period, int index)
        {
            if (index < period - 1 || index >= prices.Count)
            {
                return double.NaN;
            }

            // İlk EMA değeri, periyodun basit ortalamasıdır.
            double ema = prices.Skip(index - period + 1).Take(period).Average();

            double multiplier = 2.0 / (period + 1);
            for (int i = index - period + 1; i <= index; i++)
            {
                ema = (prices[i] - ema) * multiplier + ema;
            }
            return ema;
        }

        public static (double macd, double signal) MACD(List<double> closes, int index, int fast = 12, int slow = 26, int signalPeriod = 9)
        {
            double emaFast = EMA(closes, fast, index);
            double emaSlow = EMA(closes, slow, index);
            if (double.IsNaN(emaFast) || double.IsNaN(emaSlow)) return (double.NaN, double.NaN);

            double macd = emaFast - emaSlow;
            List<double> macdList = new List<double>();

            // MACD sinyal hattı için gerekli MACD değerlerini güvenli şekilde al
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
            // İndikatör için yeterli veri yoksa NaN döndür
            if (index < period) return double.NaN;

            double gain = 0, loss = 0;
            // Döngü başlangıcı, indeksin 0'dan büyük olmasını garantiler
            for (int i = index - period + 1; i <= index; i++)
            {
                if (i <= 0) continue;
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