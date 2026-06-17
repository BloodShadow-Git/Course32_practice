using Microsoft.EntityFrameworkCore;
using PlayerService.Data;
using PlayerService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using SharedLibrary.Middleware;

var builder = WebApplication.CreateBuilder(args);

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

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<PlayerDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddHostedService<ClickConsumerService>();

builder.Services.AddHttpClient();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();
// Добавляем Middleware для проверки API KEY (запрос должен содержать заголовок X-Api-Key)
app.UseMiddleware<ApiKeyMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<PlayerDbContext>();
    dbContext.Database.EnsureCreated(); 

    // Создаем пользователя по умолчанию из конфигурации (Environment Variables)
    var adminUser = builder.Configuration["Admin:Username"] ?? "admin";
    var adminPass = builder.Configuration["Admin:Password"] ?? "adminpass";

    if (!dbContext.Players.Any(p => p.Username == adminUser))
    {
        dbContext.Players.Add(new PlayerService.Models.Player 
        { 
            Username = adminUser, 
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPass),
            Balance = 0,
            IsAdmin = true
        });
        dbContext.SaveChanges();
    }
}

// Эндпоинт для регистрации
app.MapPost("/api/auth/register", async (LoginRequest request, PlayerDbContext db) =>
{
    if (string.IsNullOrEmpty(request.Password)) return Results.BadRequest("Пароль обязателен");
    
    if (await db.Players.AnyAsync(p => p.Username == request.Username))
    {
        return Results.BadRequest("Пользователь уже существует");
    }

    var player = new PlayerService.Models.Player 
    { 
        Username = request.Username, 
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
        Balance = 0,
        IsAdmin = request.Username.ToLower() == "admin"
    };
    db.Players.Add(player);
    await db.SaveChangesAsync();
    
    return Results.Ok(new { Success = true });
});

// Эндпоинт для авторизации и получения токена
app.MapPost("/api/auth/login", async (LoginRequest request, PlayerDbContext db, IConfiguration config) =>
{
    var player = await db.Players.FirstOrDefaultAsync(p => p.Username == request.Username);
    if (player == null || !BCrypt.Net.BCrypt.Verify(request.Password, player.PasswordHash))
    {
        return Results.BadRequest("Неверный логин или пароль");
    }
    
    if (player.IsBlocked)
    {
        return Results.BadRequest("Аккаунт заблокирован");
    }

    var issuer = config["Jwt:Issuer"];
    var audience = config["Jwt:Audience"];
    var key = Encoding.ASCII.GetBytes(config["Jwt:Key"]!);
    var tokenDescriptor = new SecurityTokenDescriptor
    {
        Subject = new ClaimsIdentity(new[]
        {
            new Claim("Id", player.Id.ToString()),
            new Claim("IsAdmin", player.IsAdmin ? "true" : "false"),
            new Claim(JwtRegisteredClaimNames.Sub, request.Username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        }),
        Expires = DateTime.UtcNow.AddMinutes(120),
        Issuer = issuer,
        Audience = audience,
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
    };
    var tokenHandler = new JwtSecurityTokenHandler();
    var token = tokenHandler.CreateToken(tokenDescriptor);
    var jwtToken = tokenHandler.WriteToken(token);

    return Results.Ok(new { Token = jwtToken, UserId = player.Id, Username = player.Username, IsAdmin = player.IsAdmin });
});

// Сгенерировать API ключ для внешнего фронтенда (только для авторизованных)
app.MapPost("/api/auth/generate-key", async (CreateKeyRequest request, PlayerDbContext db, HttpContext context) =>
{
    var userIdString = context.User.FindFirst("Id")?.Value;
    if (!int.TryParse(userIdString, out int userId)) return Results.Unauthorized();

    var player = await db.Players.Include(p => p.ApiKeys).FirstOrDefaultAsync(p => p.Id == userId);
    if (player == null) return Results.NotFound();

    var newKey = new PlayerService.Models.ApiKeyInfo
    {
        Key = Guid.NewGuid().ToString("N"),
        Name = string.IsNullOrWhiteSpace(request.Name) ? "My Key" : request.Name,
        CallsCount = 0
    };
    
    player.ApiKeys.Add(newKey);
    await db.SaveChangesAsync();

    return Results.Ok(new { ApiKey = newKey.Key, Name = newKey.Name });
}).RequireAuthorization();

// Получить список ключей
app.MapGet("/api/auth/keys", async (PlayerDbContext db, HttpContext context) =>
{
    var userIdString = context.User.FindFirst("Id")?.Value;
    if (!int.TryParse(userIdString, out int userId)) return Results.Unauthorized();

    var keys = await db.ApiKeys.Where(k => k.PlayerId == userId).ToListAsync();
    return Results.Ok(keys);
}).RequireAuthorization();

// Переключить статус своего ключа
app.MapPost("/api/auth/keys/{id}/toggle", async (int id, PlayerDbContext db, HttpContext context) =>
{
    var userIdString = context.User.FindFirst("Id")?.Value;
    if (!int.TryParse(userIdString, out int userId)) return Results.Unauthorized();

    var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.PlayerId == userId);
    if (key == null) return Results.NotFound();

    key.IsActive = !key.IsActive;
    await db.SaveChangesAsync();
    return Results.Ok();
}).RequireAuthorization();

// Удалить свой ключ
app.MapDelete("/api/auth/keys/{id}", async (int id, PlayerDbContext db, HttpContext context) =>
{
    var userIdString = context.User.FindFirst("Id")?.Value;
    if (!int.TryParse(userIdString, out int userId)) return Results.Unauthorized();

    var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.PlayerId == userId);
    if (key == null) return Results.NotFound();

    db.ApiKeys.Remove(key);
    await db.SaveChangesAsync();
    return Results.Ok();
}).RequireAuthorization();

// Эндпоинт для проверки API ключа (вызывается из Middleware других сервисов)
app.MapPost("/api/auth/validate-key", async (ValidateKeyRequest req, PlayerDbContext db) =>
{
    // dev key
    if (req.ApiKey == "dev_super_secret_api_key_123")
    {
        return Results.Ok(new { IsValid = true, Username = "SystemAdmin", KeyName = "Dev Key" });
    }

    var keyInfo = await db.ApiKeys.Include(k => k.Player).FirstOrDefaultAsync(k => k.Key == req.ApiKey);
    if (keyInfo == null)
    {
        // Ключ незарегистрирован
        return Results.NotFound(new { IsValid = false, Error = "Ключ незарегестрирован" });
    }
    
    if (!keyInfo.IsActive || keyInfo.Player == null || keyInfo.Player.IsBlocked)
    {
        // Ключ отклонён (отключен или пользователь заблокирован)
        return Results.Json(new { IsValid = false, Error = "Ключ отклонён" }, statusCode: 403);
    }
    
    keyInfo.CallsCount++;
    await db.SaveChangesAsync();
    return Results.Ok(new { IsValid = true, Username = keyInfo.Player.Username, KeyName = keyInfo.Name });
});

// Получить все ключи всех пользователей (Внутренний вызов от AdminService)
app.MapGet("/api/internal/admin/keys", async (PlayerDbContext db, HttpContext context, IConfiguration config) =>
{
    var adminApiKey = config["X-Admin-Api-Key"];
    if (context.Request.Headers["X-Admin-Api-Key"] != adminApiKey) return Results.Unauthorized();

    var keys = await db.ApiKeys.Include(k => k.Player)
        .Select(k => new { k.Id, k.Key, k.Name, k.CallsCount, k.IsActive, PlayerName = k.Player!.Username })
        .ToListAsync();
        
    return Results.Ok(keys);
});

// Переключить статус ключа (Внутренний вызов от AdminService)
app.MapPost("/api/internal/admin/keys/{id}/toggle", async (int id, PlayerDbContext db, HttpContext context, IConfiguration config) =>
{
    var adminApiKey = config["X-Admin-Api-Key"];
    if (context.Request.Headers["X-Admin-Api-Key"] != adminApiKey) return Results.Unauthorized();

    var key = await db.ApiKeys.FindAsync(id);
    if (key == null) return Results.NotFound();

    key.IsActive = !key.IsActive;
    await db.SaveChangesAsync();
    return Results.Ok();
});

// Получить всех игроков (Внутренний вызов от AdminService)
app.MapGet("/api/internal/admin/players", async (PlayerDbContext db, HttpContext context, IConfiguration config) =>
{
    var adminApiKey = config["X-Admin-Api-Key"];
    if (context.Request.Headers["X-Admin-Api-Key"] != adminApiKey) return Results.Unauthorized();

    var players = await db.Players
        .Include(p => p.ApiKeys)
        .Include(p => p.Inventory)
        .Select(p => new { 
            p.Id, 
            p.Username, 
            p.Balance, 
            p.IsAdmin, 
            p.IsBlocked,
            p.PasswordHash,
            ApiKeys = p.ApiKeys.Select(k => new { k.Id, k.Name, k.Key, k.IsActive, k.CallsCount }),
            Inventory = p.Inventory.Select(i => new { i.ItemId, i.ItemName, i.Count })
        })
        .ToListAsync();
        
    return Results.Ok(players);
});

// Заблокировать/Разблокировать игрока вместе с ключами
app.MapPost("/api/internal/admin/players/{id}/toggle-block", async (int id, PlayerDbContext db, HttpContext context, IConfiguration config) =>
{
    var adminApiKey = config["X-Admin-Api-Key"];
    if (context.Request.Headers["X-Admin-Api-Key"] != adminApiKey) return Results.Unauthorized();

    var player = await db.Players.Include(p => p.ApiKeys).FirstOrDefaultAsync(p => p.Id == id);
    if (player == null) return Results.NotFound();

    player.IsBlocked = !player.IsBlocked;
    
    // Блокируем или разблокируем все ключи пользователя вместе с его учеткой
    foreach(var key in player.ApiKeys)
    {
        key.IsActive = !player.IsBlocked;
    }

    await db.SaveChangesAsync();
    return Results.Ok();
});

// Удалить игрока со всеми данными
app.MapDelete("/api/internal/admin/players/{id}", async (int id, PlayerDbContext db, HttpContext context, IConfiguration config) =>
{
    var adminApiKey = config["X-Admin-Api-Key"];
    if (context.Request.Headers["X-Admin-Api-Key"] != adminApiKey) return Results.Unauthorized();

    var player = await db.Players.FindAsync(id);
    if (player == null) return Results.NotFound();

    db.Players.Remove(player);
    await db.SaveChangesAsync();
    return Results.Ok();
});

// Эндпоинт получения баланса и инвентаря
app.MapGet("/api/player/{id}/balance", async (int id, PlayerDbContext db, HttpContext context) =>
{
    var userIdClaim = context.User.FindFirst("Id")?.Value;
    if (userIdClaim == null || userIdClaim != id.ToString()) return Results.Forbid(); 

    var player = await db.Players.Include(p => p.Inventory).FirstOrDefaultAsync(p => p.Id == id);
    if (player == null) return Results.NotFound();
    
    return Results.Ok(new { player.Id, player.Username, player.Balance, Inventory = player.Inventory });
}).RequireAuthorization();

// Эндпоинт для списания средств и выдачи предмета (вызывается из StoreService)
app.MapPost("/api/player/{id}/spend", async (int id, SpendRequest request, PlayerDbContext db, HttpContext context) =>
{
    // Защита: этот метод могут вызывать только другие микросервисы (по-хорошему нужен другой уровень доступа, но мы упрощаем)
    var player = await db.Players.Include(p => p.Inventory).FirstOrDefaultAsync(p => p.Id == id);
    if (player == null) return Results.NotFound();

    if (player.Balance < request.Price)
    {
        return Results.BadRequest(new { Error = "Not enough points" });
    }

    player.Balance -= request.Price;
    
    var existingItem = player.Inventory.FirstOrDefault(i => i.ItemId == request.ItemId);
    if (existingItem != null)
    {
        existingItem.Count += 1;
    }
    else
    {
        player.Inventory.Add(new PlayerService.Models.PlayerItem 
        { 
            PlayerId = player.Id, 
            ItemId = request.ItemId, 
            ItemName = request.ItemName, 
            Count = 1 
        });
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { Success = true });
}).RequireAuthorization();

app.Run();

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class SpendRequest
{
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int Price { get; set; }
}

public class CreateKeyRequest
{
    public string Name { get; set; } = string.Empty;
}

public class ValidateKeyRequest
{
    public string ApiKey { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
}