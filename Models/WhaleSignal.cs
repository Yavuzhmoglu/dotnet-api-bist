namespace CoreApp.Models
{
    public class WhaleSignal
    {
        public string Symbol { get; set; } = "";
        public string Interval { get; set; } = "";
        public string Value { get; set; } = "";
        public string Open { get; set; } = "";
        public DateTime Time { get; set; }
        public string Action { get; set; } = "";
        public string Reason { get; set; } = "";

        // meta
        public int Score { get; set; }
        public double Confidence { get; set; }
        public string SuggestedAction { get; set; } = "";
    }

    // Kullanılan Candle tipi
    public record Candle(DateTime Time, double Open, double High, double Low, double Close, double Volume);
}
