using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using SharedLibrary.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Настройка HTTP клиента для связи с PlayerService
builder.Services.AddHttpClient("PlayerService", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["PlayerService:Url"] ?? "http://localhost:5002");
    client.DefaultRequestHeaders.Add("X-Admin-Api-Key", builder.Configuration["X-Admin-Api-Key"]);
    client.DefaultRequestHeaders.Add("X-Api-Key", builder.Configuration["X-Api-Key"]);
});

// Настройка JWT Аутентификации
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
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

// Получить все ключи всех пользователей (Только для Админа)
app.MapGet("/api/admin/keys", async (HttpContext context, IHttpClientFactory httpClientFactory) =>
{
    var isAdminClaim = context.User.FindFirst("IsAdmin")?.Value;
    if (isAdminClaim != "true") return Results.Forbid();

    var playerClient = httpClientFactory.CreateClient("PlayerService");
    var response = await playerClient.GetAsync("/api/internal/admin/keys");
    
    if (response.IsSuccessStatusCode)
    {
        var keys = await response.Content.ReadFromJsonAsync<object>();
        return Results.Ok(keys);
    }
    
    return Results.StatusCode((int)response.StatusCode);
}).RequireAuthorization();

// Получить всех игроков (Только для Админа)
app.MapGet("/api/admin/players", async (HttpContext context, IHttpClientFactory httpClientFactory) =>
{
    var isAdminClaim = context.User.FindFirst("IsAdmin")?.Value;
    if (isAdminClaim != "true") return Results.Forbid();

    var playerClient = httpClientFactory.CreateClient("PlayerService");
    var response = await playerClient.GetAsync("/api/internal/admin/players");
    
    if (response.IsSuccessStatusCode)
    {
        var players = await response.Content.ReadFromJsonAsync<object>();
        return Results.Ok(players);
    }
    
    return Results.StatusCode((int)response.StatusCode);
}).RequireAuthorization();

// Блокировать/Разблокировать игрока
app.MapPost("/api/admin/players/{id}/toggle-block", async (int id, HttpContext context, IHttpClientFactory httpClientFactory) =>
{
    var isAdminClaim = context.User.FindFirst("IsAdmin")?.Value;
    if (isAdminClaim != "true") return Results.Forbid();

    var playerClient = httpClientFactory.CreateClient("PlayerService");
    var response = await playerClient.PostAsync($"/api/internal/admin/players/{id}/toggle-block", null);
    
    if (response.IsSuccessStatusCode) return Results.Ok();
    return Results.StatusCode((int)response.StatusCode);
}).RequireAuthorization();

// Удалить игрока
app.MapDelete("/api/admin/players/{id}", async (int id, HttpContext context, IHttpClientFactory httpClientFactory) =>
{
    var isAdminClaim = context.User.FindFirst("IsAdmin")?.Value;
    if (isAdminClaim != "true") return Results.Forbid();

    var playerClient = httpClientFactory.CreateClient("PlayerService");
    var response = await playerClient.DeleteAsync($"/api/internal/admin/players/{id}");
    
    if (response.IsSuccessStatusCode) return Results.Ok();
    return Results.StatusCode((int)response.StatusCode);
}).RequireAuthorization();

// Переключить статус ключа (Только для Админа)
app.MapPost("/api/admin/keys/{id}/toggle", async (int id, HttpContext context, IHttpClientFactory httpClientFactory) =>
{
    var isAdminClaim = context.User.FindFirst("IsAdmin")?.Value;
    if (isAdminClaim != "true") return Results.Forbid();

    var playerClient = httpClientFactory.CreateClient("PlayerService");
    var response = await playerClient.PostAsync($"/api/internal/admin/keys/{id}/toggle", null);
    
    if (response.IsSuccessStatusCode)
    {
        return Results.Ok();
    }
    
    return Results.StatusCode((int)response.StatusCode);
}).RequireAuthorization();

app.Run();