using ArbiScannerWeb.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace ArbiScanner.TelegramNotifierApp.Infrastructure.DbContext;

public class AppDbContext(DbContextOptions<AppDbContext> options) : Microsoft.EntityFrameworkCore.DbContext(options)
{
    public DbSet<UserSettingsModel> UserSettings { get; set; }
    public DbSet<TelegramLinkRequest> TelegramLinkRequests { get; set; }
    public DbSet<UserExchangeModel> UserExchanges { get; set; }
    public DbSet<ExchangeModel> Exchanges { get; set; }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserSettingsModel>()
           .ToTable("UserSettings", t => t.ExcludeFromMigrations());

        modelBuilder.Entity<TelegramLinkRequest>()
            .ToTable("TelegramLinkRequests", t => t.ExcludeFromMigrations());

        modelBuilder.Entity<UserExchangeModel>()
            .ToTable("UserExchanges", t => t.ExcludeFromMigrations());

        modelBuilder.Entity<ExchangeModel>()
            .ToTable("Exchanges", t => t.ExcludeFromMigrations());

        base.OnModelCreating(modelBuilder);
    }
}
