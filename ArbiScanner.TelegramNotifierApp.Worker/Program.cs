using ArbiScanner.TelegramNotifierApp.Application.Services;
using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using ArbiScanner.TelegramNotifierApp.Domain.Settings;
using ArbiScanner.TelegramNotifierApp.Worker.Worker;
using ArbiScanner.TelegramNotifierApp.Worker.Worker.TelegramMessageController;
using ArbiScannerWeb.Abstractions.Interfaces;
using ArbiScannerWeb.Infrastructure.Services;
using ArbiScannerWeb.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProtoBuf.Meta;
using Serilog;
using Telegram.Bot;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var host = Host.CreateDefaultBuilder(args)
        .UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration))
        .ConfigureServices((context, services) =>
        {
            services.AddHostedService<SpreadsMessageBroker>();
            services.AddHostedService<TelegramMessageController>();

            services.AddScoped<MainController>();
            services.AddScoped<IRabbitMqService, RabbitMqService>();
            services.AddScoped<ISpreadService, SpreadService>();
            services.AddSingleton<ITelegramBotClient>(_ =>
            {
                var botToken = context.Configuration["Telegram:BotToken"];
                if (string.IsNullOrWhiteSpace(botToken))
                {
                    throw new InvalidOperationException("Telegram bot token is not configured.");
                }

                return new TelegramBotClient(botToken);
            });
            services.AddScoped<ITelegramNotifierUserService, TelegramNotifierUserService>();
            services.AddScoped<ITelegramUserService, ArbiScanner.TelegramNotifierApp.Application.Services.TelegramUserService>();

            services.AddDbContextFactory<AppDbContext>(options =>
            {
                options.UseNpgsql(context.Configuration.GetConnectionString("PostgreSqlConnection"));
            });
            services.Configure<RabbitMqSettings>(context.Configuration.GetSection("RabbitMq"));
            services.Configure<TelegramSettings>(context.Configuration.GetSection("Telegram"));
        })
        .Build();

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
