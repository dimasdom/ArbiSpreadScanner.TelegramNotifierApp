using ArbiScannerWeb.Domain.Models;
using Microsoft.EntityFrameworkCore;

public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options) { }
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