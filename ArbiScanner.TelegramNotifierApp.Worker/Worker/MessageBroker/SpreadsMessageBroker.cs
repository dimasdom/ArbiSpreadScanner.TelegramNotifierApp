
using ArbiScannerWeb.Domain.Models;
using ArbiScannerWeb.Abstractions.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using ArbiScannerWeb.Infrastructure.Services;
namespace ArbiScanner.TelegramNotifierApp.Worker.Worker;
public class SpreadsMessageBroker : BackgroundService
{
    private readonly IRabbitMqService _rabbitMqService;
    private readonly ILogger<SpreadsMessageBroker> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public SpreadsMessageBroker(
        IRabbitMqService rabbitMqService,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<SpreadsMessageBroker> logger)
    {
        _rabbitMqService = rabbitMqService;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Spreads Message Broker started");

        _rabbitMqService.OnMessageReceived += ProcessMessageAsync;

        stoppingToken.Register(() => _logger.LogInformation("Cancellation requested"));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {

                await _rabbitMqService.StartConsumingAsync(stoppingToken);
                _logger.LogInformation("Started consuming from RabbitMQ");

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (Exception ex)
            {
                await _rabbitMqService.StopConsumingAsync();
                _logger.LogError(ex, "RabbitMQ consuming failed. Will retry in 5 seconds");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        try
        {
            await _rabbitMqService.StopConsumingAsync();
            _logger.LogInformation("Stopped consuming from RabbitMQ");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping RabbitMQ consumer");
        }
    }

    private Task ProcessMessageAsync(TradeOpportunityModel possiblePosition)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var spreadService = scope.ServiceProvider.GetRequiredService<ISpreadService>();

                switch (possiblePosition.ActionType)
                {
                    case MarketPositionAction.Open:
                        await spreadService.HandleNewSpread(possiblePosition);
                        break;
                    case MarketPositionAction.Update:
                        _logger.LogInformation("Received update for spread: {Guid}", possiblePosition.Guid);
                        break;
                    case MarketPositionAction.Close:
                        await spreadService.HandleCloseSpread(possiblePosition);
                        break;
                    default:
                        _logger.LogWarning("Unknown action type: {ActionType}", possiblePosition.ActionType);
                        return;
                }

                _logger.LogInformation("Processed message: {ActionType}", possiblePosition.ActionType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing RabbitMQ message");
            }
        });

        return Task.CompletedTask;
    }
}