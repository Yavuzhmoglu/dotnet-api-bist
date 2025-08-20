namespace CoreApp.Models
{
    public class WhaleSignal
    {
        public DateTime Time { get; set; }
        public string Symbol { get; set; } = "";
        public string Interval { get; set; } = "";
        public string Action { get; set; } = "";   // "Alış", "Satış", "Toplama", "Dağıtım"
        public string Reason { get; set; } = "";   // kısa açıklama
    }
}
