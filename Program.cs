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

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
