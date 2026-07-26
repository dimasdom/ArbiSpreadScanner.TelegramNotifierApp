using System.Reflection;
using ArbiScanner.TelegramNotifierApp.Domain.Settings;
using ArbiScanner.TelegramNotifierApp.Worker.Worker.TelegramMessageController;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace ArbiScanner.TelegramNotifierApp.Tests;

public class TelegramMessageControllerTests
{
    // ExecuteAsync's happy path calls Telegram.Bot's StartReceiving, which kicks off an
    // un-awaited background polling loop against the real Telegram API using a local
    // CancellationTokenSource that is never linked to the token passed into ExecuteAsync.
    // Driving that path from a unit test would leave a real outbound-network loop running
    // for the life of the test process, so this only exercises the DI wiring that happens
    // before StartReceiving is ever reached.
    [Fact]
    public void ExecuteAsync_ResolvesMainControllerFromProvider_BeforeStartingReceiver()
    {
        var settings = Options.Create(new TelegramSettings
        {
            BotToken = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi",
            ChatId = "1",
        });
        var serviceProvider = Substitute.For<IServiceProvider>();

        var sut = new TelegramMessageController(serviceProvider, settings);
        var method = typeof(TelegramMessageController).GetMethod("ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var ex = Assert.Throws<TargetInvocationException>(() => method.Invoke(sut, [CancellationToken.None]));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        serviceProvider.Received(1).GetService(typeof(MainController));
    }
}
