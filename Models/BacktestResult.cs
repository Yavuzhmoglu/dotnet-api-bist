using System;

namespace CoreApp.Models
{
    // Offline strategy-validation DTO. Deliberately separate from WhaleSignal so live scoring
    // can never accidentally depend on forward-looking (future) data.
    public class BacktestResult
    {
        public string Symbol { get; set; } = "";
        public DateTime SignalTime { get; set; }
        public string Action { get; set; } = "";
        public double EntryPrice { get; set; }
        public double ForwardReturnPct { get; set; }
        public bool HitTakeProfit { get; set; }
        public bool HitStopLoss { get; set; }
        public double BacktestScore { get; set; } // 0-100, ATR stop-loss/take-profit grade
    }
}
