using audexa_backend.Data;
using audexa_backend.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Database Path

var appDataPath = Environment.GetFolderPath(
    Environment.SpecialFolder.ApplicationData);

var audexaDataPath = Path.Combine(appDataPath, "Audexa");

Directory.CreateDirectory(audexaDataPath);

var databasePath = Path.Combine(audexaDataPath, "audexa.db");

// Services

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy
            .WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));

builder.Services.AddScoped<IAudioFileService, AudioFileService>();
builder.Services.AddSingleton<IAudioDeviceService, AudioDeviceService>();
builder.Services.AddScoped<IAudioSettingsService, AudioSettingsService>();
builder.Services.AddScoped<IAudioDiagnosticsService, AudioDiagnosticsService>();

var app = builder.Build();

// Apply database migrations on startup so packaged desktop deployments do not
// require a separate EF CLI step.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// HTTP pipeline

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseCors("Frontend");

app.UseAuthorization();

app.MapControllers();

app.Run();