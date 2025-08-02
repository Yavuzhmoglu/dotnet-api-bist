var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp",
        policy => policy
            .WithOrigins("*")
            .AllowAnyHeader()
            .AllowAnyMethod());
});

builder.WebHost.UseUrls("http://0.0.0.0:10000");
// Swagger servisini ekle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddControllers();

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
