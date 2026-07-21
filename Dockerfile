# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /src

# Copy project files for layer-cached restore
COPY ArbiScanner.TelegramNotifierApp/ArbiScanner.TelegramNotifierApp.Worker/ArbiScanner.TelegramNotifierApp.Worker.csproj \
     ArbiScanner.TelegramNotifierApp/ArbiScanner.TelegramNotifierApp.Worker/
COPY ArbiScanner.TelegramNotifierApp/ArbiScanner.TelegramNotifierApp.Abstractions/ArbiScanner.TelegramNotifierApp.Abstractions.csproj \
     ArbiScanner.TelegramNotifierApp/ArbiScanner.TelegramNotifierApp.Abstractions/
COPY ArbiScanner.TelegramNotifierApp/ArbiScanner.TelegramNotifierApp.Application/ArbiScanner.TelegramNotifierApp.Application.csproj \
     ArbiScanner.TelegramNotifierApp/ArbiScanner.TelegramNotifierApp.Application/
COPY ArbiScanner.TelegramNotifierApp/ArbiScanner.TelegramNotifierApp.Domain/ArbiScanner.TelegramNotifierApp.Domain.csproj \
     ArbiScanner.TelegramNotifierApp/ArbiScanner.TelegramNotifierApp.Domain/
COPY ArbiScanner.TelegramNotifierApp/ArbiScanner.TelegramNotifierApp.Infrastructure/ArbiScanner.TelegramNotifierApp.Infrastructure.csproj \
     ArbiScanner.TelegramNotifierApp/ArbiScanner.TelegramNotifierApp.Infrastructure/

# Copy sibling ArbiScannerWebApp project files needed for references
COPY ArbiScannerWebApp/ArbiScannerWeb.Abstractions/ArbiScannerWeb.Abstractions.csproj \
     ArbiScannerWebApp/ArbiScannerWeb.Abstractions/
COPY ArbiScannerWebApp/ArbiScannerWeb.Domain/ArbiScannerWeb.Domain.csproj \
     ArbiScannerWebApp/ArbiScannerWeb.Domain/
COPY ArbiScannerWebApp/ArbiScannerWeb.Infrastructure/ArbiScannerWeb.Infrastructure.csproj \
     ArbiScannerWebApp/ArbiScannerWeb.Infrastructure/

# Copy ArbiScannerAdminPanel projects — Infrastructure is referenced by the Worker
COPY ArbiScannerAdminPannel/ArbiScannerAdminPanel.Infrastructure/ArbiScannerAdminPanel.Infrastructure.csproj \
     ArbiScannerAdminPannel/ArbiScannerAdminPanel.Infrastructure/
COPY ArbiScannerAdminPannel/ArbiScannerAdminPanel.Domain/ArbiScannerAdminPanel.Domain.csproj \
     ArbiScannerAdminPannel/ArbiScannerAdminPanel.Domain/
COPY ArbiScannerAdminPannel/ArbiScannerAdminPanel.Abstractions/ArbiScannerAdminPanel.Abstractions.csproj \
     ArbiScannerAdminPannel/ArbiScannerAdminPanel.Abstractions/

RUN dotnet restore ArbiScanner.TelegramNotifierApp/ArbiScanner.TelegramNotifierApp.Worker/ArbiScanner.TelegramNotifierApp.Worker.csproj

# Copy full source
COPY ArbiScanner.TelegramNotifierApp/ ./ArbiScanner.TelegramNotifierApp/
COPY ArbiScannerWebApp/ArbiScannerWeb.Abstractions/ ./ArbiScannerWebApp/ArbiScannerWeb.Abstractions/
COPY ArbiScannerWebApp/ArbiScannerWeb.Domain/ ./ArbiScannerWebApp/ArbiScannerWeb.Domain/
COPY ArbiScannerWebApp/ArbiScannerWeb.Infrastructure/ ./ArbiScannerWebApp/ArbiScannerWeb.Infrastructure/
COPY ArbiScannerAdminPannel/ArbiScannerAdminPanel.Infrastructure/ ./ArbiScannerAdminPannel/ArbiScannerAdminPanel.Infrastructure/
COPY ArbiScannerAdminPannel/ArbiScannerAdminPanel.Domain/ ./ArbiScannerAdminPannel/ArbiScannerAdminPanel.Domain/
COPY ArbiScannerAdminPannel/ArbiScannerAdminPanel.Abstractions/ ./ArbiScannerAdminPannel/ArbiScannerAdminPanel.Abstractions/

RUN dotnet publish ArbiScanner.TelegramNotifierApp/ArbiScanner.TelegramNotifierApp.Worker/ArbiScanner.TelegramNotifierApp.Worker.csproj \
    -c Release \
    -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 curl && rm -rf /var/lib/apt/lists/*
COPY --from=build-env /app/publish .
ENTRYPOINT ["dotnet", "ArbiScanner.TelegramNotifierApp.Worker.dll"]
