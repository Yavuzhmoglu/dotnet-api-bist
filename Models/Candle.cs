namespace CoreApp.Models
{
    public class Candle
    {
        public DateTime Time { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public double Volume { get; set; }

        // Hesap kolaylıkları
        public double Body => Math.Abs(Close - Open);
        public double Range => Math.Max(High - Low, 1e-9);
        public double Return => (Close - Open) / Math.Max(Open, 1e-9);
        public bool IsUp => Close >= Open;
    }
}
