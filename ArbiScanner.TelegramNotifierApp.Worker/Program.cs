using ArbiScanner.TelegramNotifierApp.Application.Services;
using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using ArbiScanner.TelegramNotifierApp.Domain.Settings;
using ArbiScanner.TelegramNotifierApp.Infrastructure.DbContext;
using ArbiScanner.TelegramNotifierApp.Worker.Worker.MessageBroker;
using ArbiScanner.TelegramNotifierApp.Worker.Worker.TelegramMessageController;
using ArbiScannerWeb.Abstractions.Interfaces;
using ArbiScannerWeb.Infrastructure.HealthChecks;
using ArbiScannerWeb.Infrastructure.Messaging;
using ArbiScannerWeb.Messaging.Kafka;
using ArbiScannerWeb.Infrastructure.Services;
using ArbiScannerWeb.Infrastructure.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using Polly;
using ProtoBuf.Meta;
using Serilog;
using Serilog.Enrichers.Span;
using StackExchange.Redis;
using Telegram.Bot;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var host = Host.CreateDefaultBuilder(args)
        .UseSerilog((ctx, cfg) =>
        {
            cfg.ReadFrom.Configuration(ctx.Configuration)
               .Enrich.WithSpan();

            // VerboseLogging:Enabled (VERBOSE_CONSOLE_LOGS in .env) raises the minimum
            // level to Debug when console output is easier to check than Grafana/Loki.
            if (ctx.Configuration.GetValue("VerboseLogging:Enabled", false))
            {
                cfg.MinimumLevel.Debug();
            }
        })
        .ConfigureServices((context, services) =>
        {
            // RabbitMQ -> Kafka migration: in Kafka mode RabbitMQ is not consumed at all (see docs/architecture/kafka-migration-plan.md).
            var consumerMode = context.Configuration.GetConsumerMode();
            if (consumerMode != MessagingConsumerMode.Kafka)
            {
                services.AddHostedService<SpreadsMessageBroker>();
                services.AddSingleton<IRabbitMqService, RabbitMqService>();
            }

            services.AddHostedService<TelegramMessageController>();

            services.AddScoped<MainController>();
            services.AddSpreadMessaging<TelegramSpreadKafkaHandler>(context.Configuration);
            services.AddSingleton<IConnectionMultiplexer>(sp =>
            {
                var redisEndpoint = context.Configuration["Redis:Endpoint"] ?? "localhost:6379";
                return ConnectionMultiplexer.ConnectAsync(redisEndpoint).GetAwaiter().GetResult();
            });
            services.AddScoped<ISpreadService, SpreadService>();
            services.AddSingleton<ITelegramBotClient>(_ =>
            {
                var botToken = context.Configuration["Telegram:BotToken"];
                return string.IsNullOrWhiteSpace(botToken)
                    ? throw new InvalidOperationException("Telegram bot token is not configured.")
                    : new TelegramBotClient(botToken);
            });
            services.AddSingleton(_ => TelegramSendResiliencePipeline.Create());
            services.AddScoped<ITelegramNotifierUserService, TelegramNotifierUserService>();
            services.AddScoped<ITelegramUserService, ArbiScanner.TelegramNotifierApp.Application.Services.TelegramUserService>();

            services.AddDbContextFactory<AppDbContext>(options =>
                options.UseNpgsql(context.Configuration.GetConnectionString("PostgreSqlConnection")));
            services.Configure<RabbitMqSettings>(context.Configuration.GetSection("RabbitMq"));
            services.Configure<TelegramSettings>(context.Configuration.GetSection("Telegram"));

            var healthChecks = services.AddHealthChecks()
                .AddCheck<DbContextFactoryHealthCheck<AppDbContext>>("postgres")
                .AddCheck<RedisHealthCheck>("redis");
            // The broker that carries spread events gates health; the other one must not. (Shadow mode adds no Kafka check on purpose.)
            if (consumerMode == MessagingConsumerMode.Kafka)
            {
                healthChecks.AddCheck<KafkaHealthCheck>("kafka");
            }
            else
            {
                healthChecks.AddCheck<RabbitMqHealthCheck>("rabbitmq");
            }

            if (context.Configuration.GetValue("Observability:Enabled", true))
            {
                services.AddOpenTelemetry()
                    .ConfigureResource(r => r.AddService("arbiscanner-telegram-notifier", serviceVersion: "1.0.0"))
                    .WithTracing(tracing => tracing
                        .AddHttpClientInstrumentation(o => o.RecordException = true)
                        .AddEntityFrameworkCoreInstrumentation()
                        .AddSource("RabbitMQ.Client.*")
                        .AddSource(KafkaTelemetry.SourceName)
                        .AddOtlpExporter())
                    .WithMetrics(metrics => metrics
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation()
                        .AddMeter(ParityMetrics.MeterName)
                        .AddMeter(ConsumerMetrics.MeterName)
                        .AddPrometheusHttpListener(o => o.UriPrefixes = ["http://+:8085/"]));
            }
        })
        .ConfigureWebHostDefaults(webBuilder =>
        {
            webBuilder.UseUrls("http://+:8090");
            webBuilder.Configure(app => app.UseHealthChecks("/health"));
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
