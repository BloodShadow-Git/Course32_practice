using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlayerService.Data;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SharedLibrary.Messages;

namespace PlayerService.Services;

// BackgroundService СЂР°Р±РѕС‚Р°РµС‚ РІ С„РѕРЅРѕРІРѕРј СЂРµР¶РёРјРµ РЅР° РїСЂРѕС‚СЏР¶РµРЅРёРё РІСЃРµРіРѕ РІСЂРµРјРµРЅРё Р¶РёР·РЅРё РїСЂРёР»РѕР¶РµРЅРёСЏ
public class ClickConsumerService : BackgroundService
{
    private readonly ConnectionFactory _factory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClickConsumerService> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public ClickConsumerService(IConfiguration config, IServiceScopeFactory scopeFactory, ILogger<ClickConsumerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _factory = new ConnectionFactory
        {
            HostName = config["RabbitMQ:Host"] ?? "localhost",
            UserName = config["RabbitMQ:Username"] ?? "user",
            Password = config["RabbitMQ:Password"] ?? "password"
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
            _logger.LogInformation("Connecting to RabbitMQ...");
            _connection = await _factory.CreateConnectionAsync();
            _channel = await _connection.CreateChannelAsync();

            await _channel.QueueDeclareAsync(queue: "click_events", durable: true, exclusive: false, autoDelete: false);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += async (sender, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                _logger.LogInformation("Received message from RabbitMQ: {Message}", message);
                
                var clickData = JsonSerializer.Deserialize<ClickMessage>(message);

                if (clickData != null)
                {
                    // Р”Р»СЏ СЂР°Р±РѕС‚С‹ СЃ Р±Р°Р·РѕР№ РґР°РЅРЅС‹С… РІРЅСѓС‚СЂРё singleton СЃРµСЂРІРёСЃР° РЅСѓР¶РЅРѕ СЃРѕР·РґР°С‚СЊ Scope
                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<PlayerDbContext>();
                    
                    var player = await dbContext.Players.FindAsync(clickData.UserId);
                    if (player == null)
                    {
                        // РРіРЅРѕСЂРёСЂСѓРµРј РєР»РёРєРё РґР»СЏ РЅРµСЃСѓС‰РµСЃС‚РІСѓСЋС‰РёС… РёРіСЂРѕРєРѕРІ
                        _logger.LogWarning($"User {clickData.UserId} not found. Ignoring clicks.");
                    }
                    else
                    {
                        player.Balance += clickData.Clicks;
                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"Updated balance for user {clickData.UserId}: +{clickData.Clicks} points. Total: {player.Balance}");
                    }
                }

                // РџРѕРґС‚РІРµСЂР¶РґР°РµРј СѓСЃРїРµС€РЅСѓСЋ РѕР±СЂР°Р±РѕС‚РєСѓ СЃРѕРѕР±С‰РµРЅРёСЏ
                await _channel.BasicAckAsync(ea.DeliveryTag, false);
            };

            await _channel.BasicConsumeAsync(queue: "click_events", autoAck: false, consumer: consumer);
            _logger.LogInformation("Listening for click_events...");
            
            // Р–РґРµРј Р·Р°РІРµСЂС€РµРЅРёСЏ СЂР°Р±РѕС‚С‹ РїСЂРёР»РѕР¶РµРЅРёСЏ
            await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RabbitMQ Consumer. Retrying in 5 seconds...");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }
    
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel != null) await _channel.CloseAsync(cancellationToken);
        if (_connection != null) await _connection.CloseAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}
