using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SharedLibrary.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// Настройка JWT
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddSingleton<ConnectionFactory>(sp => 
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new ConnectionFactory
    {
        HostName = config["RabbitMQ:Host"] ?? "localhost",
        UserName = config["RabbitMQ:Username"] ?? "user",
        Password = config["RabbitMQ:Password"] ?? "password"
    };
});

var app = builder.Build();
app.UseCors();
app.UseMiddleware<ApiKeyMiddleware>(); // Проверка API Key для всех запросов
app.UseAuthentication();
app.UseAuthorization();

// API endpoint для приема кликов (только авторизованные пользователи)
app.MapPost("/api/click", async (ClickRequest request, ConnectionFactory factory, HttpContext context) =>
{
    // Берем ID пользователя из токена, чтобы никто не мог накрутить клики другому
    var userIdString = context.User.FindFirst("Id")?.Value;
    if (!int.TryParse(userIdString, out int userId)) return Results.Unauthorized();

    try
    {
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(queue: "click_events", durable: true, exclusive: false, autoDelete: false);
        
        // Передаем в RabbitMQ безопасный userId из токена
        var message = JsonSerializer.Serialize(new { UserId = userId, Clicks = request.Clicks });
        var body = Encoding.UTF8.GetBytes(message);
        
        app.Logger.LogInformation("Publishing click for User {UserId}: {Clicks} clicks", userId, request.Clicks);
        
        await channel.BasicPublishAsync(exchange: string.Empty, routingKey: "click_events", body: body);
        
        return Results.Ok(new { Status = "Clicks queued", userId, request.Clicks });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to publish");
        return Results.StatusCode(500);
    }
}).RequireAuthorization();

app.Run();

public class ClickRequest
{
    public int Clicks { get; set; }
}