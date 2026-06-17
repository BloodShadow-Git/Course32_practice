using Microsoft.EntityFrameworkCore;
using StoreService.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using StoreService.Models;
using SharedLibrary.Middleware;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<StoreDbContext>(options => options.UseNpgsql(connectionString));

// Настройка HTTP клиента для связи с PlayerService
builder.Services.AddHttpClient("PlayerService", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["PlayerService:Url"] ?? "http://localhost:5002");
    client.DefaultRequestHeaders.Add("X-Api-Key", builder.Configuration["X-Api-Key"]);
});

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

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();
app.UseMiddleware<ApiKeyMiddleware>(); // Проверка API Key для всех запросов
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
    dbContext.Database.EnsureCreated(); 
}

// Получить каталог товаров
app.MapGet("/api/store/items", async (StoreDbContext db) =>
{
    var items = await db.Items.ToListAsync();
    return Results.Ok(items);
}).RequireAuthorization(); // Только для авторизованных

// Купить товар
app.MapPost("/api/store/buy", async (BuyRequest request, StoreDbContext db, HttpContext context, IHttpClientFactory httpClientFactory) =>
{
    var userIdString = context.User.FindFirst("Id")?.Value;
    if (!int.TryParse(userIdString, out int userId)) return Results.Unauthorized();

    var item = await db.Items.FindAsync(request.ItemId);
    if (item == null) return Results.NotFound("Item not found");

    // Обращаемся к PlayerService чтобы проверить баланс (Синхронное взаимодействие микросервисов)
    // В идеальной архитектуре здесь был бы паттерн Сага или двухфазный коммит, 
    // но для учебного проекта используем простой HTTP-вызов.
    var playerClient = httpClientFactory.CreateClient("PlayerService");
    
    // Передаем JWT токен текущего пользователя в PlayerService
    var token = context.Request.Headers.Authorization.ToString();
    playerClient.DefaultRequestHeaders.Add("Authorization", token);
    
    // Вызываем эндпоинт списания средств и выдачи предмета
    var spendResponse = await playerClient.PostAsJsonAsync($"/api/player/{userId}/spend", new { ItemId = item.Id, ItemName = item.Name, Price = item.Price });
    
    if (!spendResponse.IsSuccessStatusCode)
    {
        var errorResult = await spendResponse.Content.ReadAsStringAsync();
        return Results.BadRequest(new { Error = "Failed to buy item: " + errorResult });
    }
    
    return Results.Ok(new { Status = "Success", Message = $"You bought {item.Name}!" });
}).RequireAuthorization();

app.Run();

public class BuyRequest
{
    public int ItemId { get; set; }
}

public class PlayerInfo
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public int Balance { get; set; }
}