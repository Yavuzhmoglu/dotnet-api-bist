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
builder.Services.Configure<AccumulationWeights>(builder.Configuration.GetSection("AccumulationWeights"));


builder.WebHost.UseUrls("http://0.0.0.0:10000");
// Swagger servisini ekle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddControllers();
builder.Services.AddMemoryCache();


builder.Services.AddScoped<WhaleIntelService>();
// HttpClient DI (Yahoo i�in)
builder.Services.AddHttpClient();

// WhaleIntelService DI

var app = builder.Build();

app.UseCors("AllowReactApp");
// Geli�tirme ortam�nda Swagger'� etkinle�tir
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Defense in depth: any unhandled exception returns JSON instead of a bare empty 500,
// and gets logged so failures are visible in Render's logs.
app.UseExceptionHandler(errApp =>
{
    errApp.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(feature?.Error, "Unhandled exception on {Path}", feature?.Path);

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "Sunucu hatası oluştu." });
    });
});

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
