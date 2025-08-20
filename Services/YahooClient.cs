using CoreApp.Models;
using System.Net;
using System.Text.Json;

namespace CoreApp.Services
{
    public static class YahooClient
    {
        private static readonly HttpClient _http;
        private static readonly Uri _base = new("https://query1.finance.yahoo.com/");
        private static readonly SemaphoreSlim _yahooGate = new(2, 2); // aynı anda en fazla 2 istek

        static YahooClient()
        {
            _http = new HttpClient
            {
                BaseAddress = _base,
                Timeout = TimeSpan.FromSeconds(120)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("BalinaTespitiApi/1.0");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }

        public static async Task<List<Candle>> GetCandlesAsync(string symbol, string interval, string range)
        {
            var path = $"v8/finance/chart/{symbol}?interval={interval}&range={range}";
            await _yahooGate.WaitAsync();

            try
            {
                //using (HttpClient client = new HttpClient()) ;
                
                //string url = $"https://query1.finance.yahoo.com/v8/v8/finance/chart/{symbol}?interval={interval}&range={range}";
                //// User-Agent eklemek bazen işe yarar
                //client.DefaultRequestHeaders.Add("User-Agent", "CSharpApp");

                //HttpResponseMessage response = await client.GetAsync(url);

                // Basit retry (429/503)
                for (int attempt = 1; ; attempt++)
                {
                    var resp = await _http.GetAsync(path);
                    if (resp.IsSuccessStatusCode)
                    {
                        await using var stream = await resp.Content.ReadAsStreamAsync();
                        using var doc = await JsonDocument.ParseAsync(stream);

                        var chart = doc.RootElement.GetProperty("chart");
                        if (chart.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                            throw new Exception(err.ToString());

                        var result = chart.GetProperty("result")[0];
                        int tzOffset = 0;
                        if (result.TryGetProperty("meta", out var meta) && meta.TryGetProperty("gmtoffset", out var gmto))
                            tzOffset = gmto.GetInt32();

                        var timestamps = result.GetProperty("timestamp").EnumerateArray().Select(t => t.GetInt64()).ToArray();
                        var quote = result.GetProperty("indicators").GetProperty("quote")[0];

                        var opens = quote.GetProperty("open").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToArray();
                        var highs = quote.GetProperty("high").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToArray();
                        var lows = quote.GetProperty("low").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToArray();
                        var closes = quote.GetProperty("close").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToArray();
                        var volumes = quote.GetProperty("volume").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0.0).ToArray();

                        int n = timestamps.Length;
                        var list = new List<Candle>(n);
                        for (int i = 0; i < n; i++)
                        {
                            if (double.IsNaN(opens[i]) || double.IsNaN(highs[i]) || double.IsNaN(lows[i]) || double.IsNaN(closes[i]))
                                continue;

                            var utc = DateTimeOffset.FromUnixTimeSeconds(timestamps[i]);
                            var local = utc.ToOffset(TimeSpan.FromSeconds(tzOffset)).DateTime;

                            list.Add(new Candle
                            {
                                Time = local,
                                Open = opens[i],
                                High = highs[i],
                                Low = lows[i],
                                Close = closes[i],
                                Volume = volumes[i]
                            });
                        }
                        return list;
                    }

                    if ((int)resp.StatusCode == 429 || resp.StatusCode == HttpStatusCode.ServiceUnavailable)
                    {
                        var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.Zero;
                        var delay = retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt - 1));
                        if (attempt >= 3) resp.EnsureSuccessStatusCode();
                        await Task.Delay(delay);
                        continue;
                    }

                    resp.EnsureSuccessStatusCode();
                }
            }
            finally
            {
                _yahooGate.Release();
            }
        }
    }
}