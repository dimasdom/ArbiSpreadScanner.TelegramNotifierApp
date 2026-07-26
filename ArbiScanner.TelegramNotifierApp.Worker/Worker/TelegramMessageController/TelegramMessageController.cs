using ArbiScanner.TelegramNotifierApp.Domain.Settings;
using ArbiScanner.TelegramNotifierApp.Worker.Worker.TelegramMessageController;
using ArbiScannerWeb.Domain.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace ArbiScanner.TelegramNotifierApp.Worker.Worker.TelegramMessageController;
public class TelegramMessageController(IServiceProvider serviceProvider, IOptions<TelegramSettings> settings) : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IOptions<TelegramSettings> _settings = settings;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var cts = new CancellationTokenSource();
        var bot = new TelegramBotClient(_settings.Value.BotToken, cancellationToken: cts.Token);
        MainController mainController = _serviceProvider.GetRequiredService<MainController>();
        bot.StartReceiving(
            mainController.Index,
            (_, exception, _, _) => MainController.HandleErrorAsync(exception),
            cancellationToken: cts.Token);
        return Task.CompletedTask;
    }
}


