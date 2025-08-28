namespace CoreApp.Models
{
    public class WhaleSignal
    {
        public string Symbol { get; set; } = "";
        public string Interval { get; set; } = "";
        public string Value { get; set; } = "";    // close as string
        public string Open { get; set; } = "";
        public DateTime Time { get; set; }
        public string Action { get; set; } = "";   // Alış, Satış, Toplama, Dağıtım, Öngörü
        public string Reason { get; set; } = "";

        // Scoring & meta
        public int Score { get; set; } = 0;        // 0..100
        public double Confidence { get; set; } = 0; // 0..1
        public int Confirmations { get; set; } = 0;
        public string SuggestedAction { get; set; } = "";

        // Prediction fields (optional)
        public double? PredictedUpProbability { get; set; }
        public double? ExpectedReturnPct { get; set; }
        public int PredictionHorizonBars { get; set; } = 5;
    }
}
