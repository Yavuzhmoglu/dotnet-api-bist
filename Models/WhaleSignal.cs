namespace CoreApp.Models
{
    public class WhaleSignal
    {
        public string Symbol { get; set; } = "";
        public string Interval { get; set; } = "";
        public DateTime Time { get; set; }
        public string Value { get; set; }
        public string Action { get; set; } = "";   // "Alış", "Satış", "Toplama", "Dağıtım", "Bekle", "Hata"
        public string Reason { get; set; } = "";   // Neden bu sinyal üretildi
        public double Score { get; set; }          // Canlı anomali büyüklüğü (0-100): 0=normal, 100=aşırı
        public double Confidence { get; set; }     // Bileşenlerin skorun yönüyle ne kadar uyuştuğu (0-100)
        public bool DataSufficient { get; set; } = true; // false => geçmiş veri yetersiz, bilgi amaçlı satır

        // Şeffaflık için alt skor bileşenleri (frontend güvenle yok sayabilir)
        public double VolumeZ { get; set; }
        public double CompressionPercentile { get; set; }
        public double Cmf { get; set; }
        public double CmfSlope { get; set; }
        public double VwapDeviationPct { get; set; }
    }
}