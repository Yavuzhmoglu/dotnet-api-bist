using CoreApp.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp",
        policy => policy
            .WithOrigins("*")
            .AllowAnyHeader()
            .AllowAnyMethod());
});
builder.Services.AddHttpClient<WhaleIntelService>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(c =>
    {
        c.Timeout = TimeSpan.FromSeconds(60); // 60 saniye timeout
    });




builder.WebHost.UseUrls("http://0.0.0.0:10000");
// Swagger servisini ekle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddControllers();
builder.Services.AddMemoryCache();


builder.Services.AddScoped<WhaleIntelService>();
// HttpClient DI (Yahoo için)
builder.Services.AddHttpClient();

// WhaleIntelService DI

var app = builder.Build();

app.UseCors("AllowReactApp");
// Geliþtirme ortamýnda Swagger'ý etkinleþtir
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
