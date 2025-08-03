using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace CoreApp
{
    [Route("[controller]")]
    [ApiController]
    public class DataController : ControllerBase
    {
        [HttpPost("greet")]
        public IActionResult GreetUser([FromBody] DataRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Symbol))
            {
                return BadRequest("İsim boş olamaz.");
            }

            string message = $"Merhaba, {request.Symbol}!" + (request.Symbol == "Selcan" ? " Seni Seviyorum" : "");
            return Ok(new { Message = message });
        }

        [HttpPost("history")]
        public async Task<IActionResult> GetHistory([FromBody] DataRequest request)
        {
            if (string.IsNullOrEmpty(request.Symbol))
                return BadRequest("Symbol boş olamaz.");

            using (HttpClient client = new HttpClient())
            {
                try
                {
                    var result = new List<HisseVerisi>();
                    string url = "https://query1.finance.yahoo.com/v8/finance/chart/" + request.Symbol + "?interval=" + request.Interval + "&range=" + request.Range;
                    // User-Agent eklemek bazen işe yarar
                    client.DefaultRequestHeaders.Add("User-Agent", "CSharpApp");

                    HttpResponseMessage response = await client.GetAsync(url);
                    response.EnsureSuccessStatusCode(); // hata varsa fırlatır

                    string responseBody = await response.Content.ReadAsStringAsync();

                    // JSON veriyi .NET objesine deserialize edebilirsin
                    using JsonDocument doc = JsonDocument.Parse(responseBody);
                    JsonElement root = doc.RootElement;


                    var chart = root.GetProperty("chart").GetProperty("result")[0];

                    var timestamps = chart.GetProperty("timestamp");
                    var quote = chart.GetProperty("indicators").GetProperty("quote")[0];

                    var opens = quote.GetProperty("open");
                    var highs = quote.GetProperty("high");
                    var lows = quote.GetProperty("low");
                    var closes = quote.GetProperty("close");
                    var volumes = quote.GetProperty("volume");

                    for (int i = 0; i < timestamps.GetArrayLength(); i++)
                    {
                        // Tüm değerleri null ise atla
                        if (IsNull(opens[i]) && IsNull(highs[i]) && IsNull(lows[i]) && IsNull(closes[i]) && IsNull(volumes[i]))
                            continue;

                        var hisse = new HisseVerisi
                        {
                            Tarih = DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime.AddHours(3), // TRT için +3 saat
                            Acilis = opens[i].ValueKind != JsonValueKind.Null ? opens[i].GetDecimal() : (decimal?)null,
                            Yüksek = highs[i].ValueKind != JsonValueKind.Null ? highs[i].GetDecimal() : (decimal?)null,
                            Dusuk = lows[i].ValueKind != JsonValueKind.Null ? lows[i].GetDecimal() : (decimal?)null,
                            Kapanis = closes[i].ValueKind != JsonValueKind.Null ? closes[i].GetDecimal() : (decimal?)null,
                            Hacim = volumes[i].ValueKind != JsonValueKind.Null ? volumes[i].GetInt64() : (long?)null
                        };

                        result.Add(hisse);
                    }

                    return Ok(result.OrderByDescending(n=>n.Tarih));
                }
                catch (Exception ex)
                {
                    return StatusCode(500, $"Veri alınırken hata oluştu: {ex.Message}");
                }
            }
        }
        private bool IsNull(JsonElement el)
        {
            return el.ValueKind == JsonValueKind.Null;
        }
    }
}
