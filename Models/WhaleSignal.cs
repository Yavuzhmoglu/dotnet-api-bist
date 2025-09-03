namespace CoreApp.Models
{
    public class WhaleSignal
    {
        public string Symbol { get; set; } = "";
        public string Interval { get; set; } = "";
        public DateTime Time { get; set; }
        public string Action { get; set; } = "";   // "Al", "Sat", "Bekle", "Hata"
        public string Reason { get; set; } = "";   // Neden bu sinyal üretildi
        public double Score { get; set; }          // Backtest skoru (0-100)
        public double Confidence { get; set; }     // Güven yüzdesi (0-100)
    }
}
