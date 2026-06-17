using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace SharedLibrary.Middleware
{
    public class ValidateKeyResponse 
    {
        public bool IsValid { get; set; }
        public string Username { get; set; } = string.Empty;
        public string KeyName { get; set; } = string.Empty;
    }

    public class ApiKeyMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ApiKeyMiddleware> _logger;
        private const string APIKEYNAME = "X-Api-Key";

        public ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (context.Request.Path.StartsWithSegments("/api/auth/validate-key"))
            {
                await _next(context);
                return;
            }

            string apiKey = "Unknown";

            if (context.Request.Headers.TryGetValue(APIKEYNAME, out var extractedApiKey))
            {
                apiKey = extractedApiKey.ToString();
            }
            else if (context.Request.Query.TryGetValue("api_key", out var queryApiKey))
            {
                apiKey = queryApiKey.ToString();
            }
            else
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("API Key is missing.");
                return;
            }

            // Обращаемся к PlayerService для проверки ключа
            var config = context.RequestServices.GetRequiredService<IConfiguration>();
            var playerServiceUrl = config["PlayerService:Url"] ?? "http://player-service:8080";
            
            // Если мы уже внутри PlayerService, то мы могли бы вызвать базу напрямую. 
            // Но чтобы Middleware оставался универсальным, сделаем HTTP запрос 
            // к самому себе (если мы PlayerService) или к соседу (если мы Store/Click).
            var httpClientFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
            var client = httpClientFactory.CreateClient();
            
            var validateReq = new { ApiKey = apiKey, Action = $"{context.Request.Method} {context.Request.Path}" };
            var response = await client.PostAsJsonAsync($"{playerServiceUrl}/api/auth/validate-key", validateReq);
            
            if (!response.IsSuccessStatusCode)
            {
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = "text/plain; charset=utf-8";

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    await context.Response.WriteAsync("Ключ незарегестрирован");
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    await context.Response.WriteAsync("Ключ отклонён");
                }
                else
                {
                    await context.Response.WriteAsync("Unauthorized API Key");
                }
                return;
            }

            var validateResult = await response.Content.ReadFromJsonAsync<ValidateKeyResponse>();

            _logger.LogInformation("API_USAGE_LOG: User '{Username}' using Key '{KeyName}' ({ApiKey}) performed action: {Action}", 
                validateResult?.Username, validateResult?.KeyName, apiKey, $"{context.Request.Method} {context.Request.Path}");

            await _next(context);
        }
    }
}