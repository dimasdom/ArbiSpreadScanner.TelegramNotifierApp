# ArbiScanner.TelegramNotifierApp
[![Quality gate](https://sonarcloud.io/api/project_badges/quality_gate?project=dimasdom_ArbiSpreadScanner.TelegramNotifierApp)](https://sonarcloud.io/summary/new_code?id=dimasdom_ArbiSpreadScanner.TelegramNotifierApp)

A .NET 10 background worker service that bridges the ArbiScanner platform with Telegram. It serves two purposes:

1. **Spread notification delivery** — consumes spread events from a RabbitMQ fanout exchange and delivers formatted Telegram messages to every subscribed user whose filter criteria match the incoming opportunity.
2. **Telegram bot interaction** — runs a Telegram bot polling loop that lets users link or unlink their Telegram account to their ArbiScanner web account and control notification preferences without leaving Telegram.

---

## Table of Contents

- [Overview](#overview)
- [Telegram Bot Commands](#telegram-bot-commands)
- [RabbitMQ Message Flow](#rabbitmq-message-flow)
- [Project Layer Responsibilities](#project-layer-responsibilities)
- [Technologies](#technologies)
- [Prerequisites](#prerequisites)
- [Running Locally](#running-locally)
- [Environment Variables](#environment-variables)
- [Docker Build](#docker-build)
- [CI/CD](#cicd)
- [Testing](#testing)
- [Project Structure](#project-structure)

---

## Overview

ArbiScanner.TelegramNotifierApp is a standalone worker process that runs alongside the ArbiScanner web application. It shares the `ArbiScannerBot` PostgreSQL database with the web app and reads user configuration (linked chat IDs, exchange filters, spread-size thresholds, and notification preferences) from that shared store.

When a new spread opportunity is detected by the core scanner engine, the engine publishes a protobuf-serialized `TradeOpportunityModel` message to a RabbitMQ fanout exchange. This service consumes those messages, evaluates each user's stored criteria against the incoming opportunity, constructs a formatted message, and sends it to every qualifying user via the Telegram Bot API.

In parallel, a second hosted service runs the Telegram bot polling loop. Users send commands or link codes to the bot, and the service resolves those interactions through the same shared database.

---

## Telegram Bot Commands

The bot accepts both slash commands and keyboard button presses.

| Input | Behaviour |
|---|---|
| `<link-id>` (a UUID) | Links the sender's Telegram account to the ArbiScanner web account identified by the link ID. The link ID is generated on the ArbiScanner web application. On success, the main keyboard is displayed. On failure, an error message is returned. |
| Resume button | Resumes notifications for the linked account (`active = true`). |
| Pause button | Pauses notifications without removing the account link (`active = false`). |
| Any other text | Returns usage instructions and displays the main keyboard. |

After a successful account link, the bot shows a persistent reply keyboard with **Resume** and **Pause** buttons. These buttons map to `UpdateTelegramUserActiveStatus` calls in the database and allow users to toggle delivery without going through the web application.

The account linking flow:

1. The user generates a link code on the ArbiScanner web application.
2. The user sends that UUID string to the Telegram bot.
3. `MainController` detects a valid UUID and calls `ITelegramUserService.LinkTelegramUserWithAccount`, which validates the link code in the database and associates the Telegram chat ID with the corresponding ArbiScanner user account.
4. The bot confirms success and activates notification delivery.

---

## RabbitMQ Message Flow

```
ArbiScanner scanner engine
        |
        | publishes TradeOpportunityModel (protobuf)
        v
spread_fanout_exchange  (type: fanout)
        |
        | bound queue
        v
spread_telegram  (durable queue, dead-letters to spread_telegram_dlq)
        |
        | consumed by SpreadsMessageBroker (BackgroundService), via the shared
        | RabbitMqService (see below) — dedupes, retries, acks only after this
        | actually finishes, not before
        v
ISpreadService.HandleNewSpread / HandleCloseSpread
        |
        | queries PostgreSQL for users matching:
        |   - active = true
        |   - user's selected exchanges include both legs of the spread
        |   - spread size >= user's minimum spread threshold
        |   - order-book volume >= user's position size * 3 (both sides, both legs)
        |   - spread type enabled (Spot, Futures, or Funding)
        v
ITelegramNotifierUserService.NotifyUser (per matching chat ID)
        |
        v
Telegram Bot API  ->  user's Telegram client
```

**Exchange topology:**
- Exchange name: `spread_fanout_exchange`
- Exchange type: fanout
- Queue: `spread_telegram`
- Routing key: (empty — fanout ignores routing keys)
- Message format: protobuf-serialized `TradeOpportunityModel`

**Message action types handled:**

| `ActionType` | Handler behaviour |
|---|---|
| `Open` | Constructs a formatted message and notifies all matching users. |
| `Update` | Logged and skipped (no notification sent). |
| `Close` | Logged to console; currently a no-op for notifications. |

**Message formats by spread type:**

- **Futures** — Coin, spread %, long/short exchange with prices, slippage per leg, and per-exchange funding rates.
- **Funding** — Coin, funding spread %, rate spread %, long/short exchange with prices, slippage, funding rates, and possible profit %.
- **Spot** — Coin, spot spread %, spot and futures exchange prices, slippage per leg, funding rate, and possible profit %.

> These messages previously also included a "Volatility (30m)" figure and a derived Safe/Medium/Risky/Dangerous risk-level label (volatility-to-spread ratio thresholds at 15/30/50%). That scoring was cut platform-wide — the `Volatility` field was removed from `TradeOpportunityModel`/`ExchangeRateModel` in ArbitrageScanner, so this service no longer has a value to read or format. `SpreadService` was simplified accordingly; see [Testing](#testing) for the tests covering the current message format.

---

## Project Layer Responsibilities

The solution follows Clean Architecture and is split into five projects.

### ArbiScanner.TelegramNotifierApp.Domain

Contains POCO configuration models. The only model at this layer is `TelegramSettings`, which holds the bot token and optional chat ID read from configuration. This project has no external NuGet dependencies.

### ArbiScanner.TelegramNotifierApp.Abstractions

Defines the service contracts consumed by upper layers:

- `ISpreadService` — `HandleNewSpread(TradeOpportunityModel)` and `HandleCloseSpread(TradeOpportunityModel)`. Called by the RabbitMQ consumer when a spread message arrives.
- `ITelegramNotifierUserService` — `NotifyUser(long chatId, string message)`. Sends a single Telegram message to one chat ID.
- `ITelegramUserService` — `LinkTelegramUserWithAccount`, `UnlinkTelegramUser`, and `UpdateTelegramUserActiveStatus`. Manages the Telegram-to-account association in the database.

References: `ArbiScannerWeb.Domain` (for `TradeOpportunityModel`), `FluentResults`.

### ArbiScanner.TelegramNotifierApp.Infrastructure

Provides the EF Core `AppDbContext` targeting PostgreSQL via Npgsql. Contains the database context and repositories for the Telegram user link table that joins ArbiScanner user IDs with Telegram chat IDs.

Key packages: `Microsoft.EntityFrameworkCore` 10, `Npgsql.EntityFrameworkCore.PostgreSQL`, `FluentResults`.

### ArbiScanner.TelegramNotifierApp.Application

Implements the service interfaces from Abstractions:

- `SpreadService` — receives `TradeOpportunityModel` objects, queries the database for users whose stored filter criteria match the opportunity, formats the appropriate message string (Futures, Funding, or Spot), and calls `ITelegramNotifierUserService` for each qualifying chat ID.
- `TelegramNotifierUserService` — wraps `ITelegramBotClient.SendMessage` to deliver a message to a single chat ID.
- `TelegramUserService` — validates link codes against the database, writes or removes the Telegram-to-account association, and updates the active status flag.

References: Abstractions, Infrastructure, `ArbiScannerWeb.Domain`. Packages: `Telegram.Bot` 22.x, `FluentResults`.

### ArbiScanner.TelegramNotifierApp.Worker

Entry point and host. `Program.cs` wires up all DI registrations and starts the generic host.

Two hosted services run concurrently:

- **`SpreadsMessageBroker`** (`Worker/MessageBroker/`) — a `BackgroundService` that connects to RabbitMQ, binds to the fanout exchange queue, and dispatches each received protobuf message to `ISpreadService`. If the *connection* itself fails, it stops the consumer, waits five seconds, and reconnects. Message-level retry, dead-lettering, and idempotency all live one layer down, in the shared `RabbitMqService` (`ArbiScannerWeb.Infrastructure`, referenced via project reference — the same class the WebApp uses): it awaits `ISpreadService`'s handler fully before acking (previously it fired the handler via `Task.Run` and acked immediately, so a message could be acked before it was actually processed), retries transient failures with Polly, dead-letters after exhausting retries instead of requeuing forever, and de-duplicates redelivered messages via a Redis `SET NX` claim.
- **`TelegramMessageController`** (`Worker/TelegramMessageController/`) — a `BackgroundService` that instantiates a `TelegramBotClient` and starts long-polling via `StartReceiving`. Each received update is dispatched to `MainController.Index`, which routes text messages to the appropriate handler (link code, resume, pause, or fallback).

Also references: `ArbiScannerAdminPanel.Infrastructure` (shared DbContext and models), `ArbiScannerWeb.Abstractions`, `ArbiScannerWeb.Domain`.

Packages: `Telegram.Bot`, `RabbitMQ.Client`, `Serilog` with `Serilog.Sinks.GrafanaLoki`, `Microsoft.EntityFrameworkCore` (SQL Server + PostgreSQL providers).

---

## Technologies

| Technology | Role |
|---|---|
| .NET 10 | Runtime and generic host |
| Telegram.Bot 22.x | Telegram Bot API client (polling mode) |
| RabbitMQ.Client | AMQP consumer for spread events |
| protobuf-net | Deserializing `TradeOpportunityModel` messages |
| Entity Framework Core 10 | ORM for PostgreSQL |
| Npgsql | PostgreSQL driver for EF Core |
| StackExchange.Redis | Idempotency dedupe for redelivered RabbitMQ messages (via the shared `RabbitMqService`) — this service had no Redis dependency before |
| Polly.Core | Retry (exponential backoff + jitter) around message processing, via the shared `RabbitMqService` |
| Microsoft.Extensions.Diagnostics.HealthChecks | Postgres/Redis/RabbitMQ checks exposed at `/health` on a minimal web host (port 8090), alongside the existing `/metrics` listener on 8085 |
| FluentResults | Result-type error handling across service calls |
| Serilog | Structured logging |
| Serilog.Sinks.GrafanaLoki | Log shipping to Grafana Loki |
| Serilog.Enrichers.Span | Enriches log events with `TraceId` / `SpanId` for trace-to-log correlation |
| OpenTelemetry SDK | Distributed tracing and metrics |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | OTLP gRPC export of traces to Grafana Tempo |
| OpenTelemetry.Exporter.Prometheus.HttpListener | Standalone `/metrics` HTTP server on port 8085 for Prometheus scraping |
| Docker | Containerised deployment |

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- PostgreSQL 17 (database: `ArbiScannerBot`)
- RabbitMQ 3.x with management plugin
- Redis (used for RabbitMQ message-dedupe — see [RabbitMQ Message Flow](#rabbitmq-message-flow))
- A Telegram bot token obtained from [@BotFather](https://t.me/BotFather)
- The ArbiScanner web application database must be migrated and reachable (the link table is shared)

---

## Running Locally

1. Clone the repository and initialise submodules:

   ```bash
   git clone <repo-url>
   git submodule update --init --recursive
   ```

2. Ensure PostgreSQL, RabbitMQ, and Redis are running and accessible.

3. Configure `appsettings.json` (or `appsettings.Development.json`) in `ArbiScanner.TelegramNotifierApp.Worker/`:

   ```json
   {
     "ConnectionStrings": {
       "PostgreSqlConnection": "Host=localhost;Port=5432;Database=ArbiScannerBot;Username=postgres;Password=yourpassword"
     },
     "RabbitMq": {
       "Host": "localhost",
       "Queue": "spread_telegram",
       "Username": "guest",
       "Password": "guest",
       "Exchange": "spread_fanout_exchange",
       "RoutingKey": ""
     },
     "Redis": {
       "Endpoint": "localhost:6379"
     },
     "Telegram": {
       "BotToken": "your-bot-token-here"
     },
     "OpenTelemetry": {
       "Endpoint": "http://localhost:4317"
     },
     "Serilog": {
       "WriteTo": [
         { "Name": "Console" },
         {
           "Name": "GrafanaLoki",
           "Args": {
             "uri": "http://localhost:3100",
             "labels": [
               { "key": "app", "value": "arbiscanner-telegram-notifier" },
               { "key": "env", "value": "development" }
             ],
             "propertiesAsLabels": [ "level" ]
           }
         }
       ]
     }
   }
   ```

4. Run the worker from the repository root:

   ```bash
   cd ArbiScanner.TelegramNotifierApp/ArbiScanner.TelegramNotifierApp.Worker
   dotnet run
   ```

---

## Environment Variables

All `appsettings.json` keys can be overridden with environment variables using the standard ASP.NET Core double-underscore convention.

| Variable | Description |
|---|---|
| `TELEGRAM_BOT_TOKEN` | BotFather token for the Telegram bot. Maps to `Telegram__BotToken`. |
| `ConnectionStrings__PostgreSqlConnection` | Full PostgreSQL connection string for the `ArbiScannerBot` database. |
| `RabbitMq__Host` | Hostname or IP of the RabbitMQ broker. |
| `RabbitMq__Queue` | Queue name (default: `spread_telegram`). |
| `RabbitMq__Username` | RabbitMQ username. |
| `RabbitMq__Password` | RabbitMQ password. |
| `RabbitMq__Exchange` | Exchange name (default: `spread_fanout_exchange`). |
| `RabbitMq__RoutingKey` | Routing key (empty string for fanout exchanges). |
| `Redis__Endpoint` | Redis connection string, e.g. `redis:6379`. Used for RabbitMQ message-dedupe (see [RabbitMQ Message Flow](#rabbitmq-message-flow)). |
| `Serilog__WriteTo__1__Args__uri` | Grafana Loki endpoint URL (e.g. `http://loki:3100`). |
| `OpenTelemetry__Endpoint` | OTLP gRPC endpoint for Grafana Tempo (e.g. `http://tempo:4317`). Defaults to `http://localhost:4317` from `appsettings.json`. |

---

## Docker Build

The Dockerfile uses a multi-stage build. The build context must be the **repository root** because the image references sibling projects (`ArbiScannerWebApp`, `ArbiScannerAdminPannel`) that live outside the `ArbiScanner.TelegramNotifierApp/` directory.

**Build the image manually:**

```bash
# From the repository root
docker build \
  -f ArbiScanner.TelegramNotifierApp/Dockerfile \
  -t arbiscanner-telegram-notifier:latest \
  .
```

**Run with Docker Compose (recommended):**

The `docker-compose.yml` inside `ArbiScanner.TelegramNotifierApp/` starts the worker together with PostgreSQL 17, RabbitMQ 3 (with the management UI on port 15672), and Redis. The worker waits for all three to pass their health checks before starting.

```bash
# From ArbiScanner.TelegramNotifierApp/
TELEGRAM_BOT_TOKEN=your-bot-token docker compose up --build
```

The compose file sets health checks for PostgreSQL (`pg_isready`), RabbitMQ (`rabbitmq-diagnostics ping`), and Redis (`redis-cli ping`), and restarts all services unless stopped manually. The worker itself also exposes `/health` (Postgres/Redis/RabbitMQ) on a minimal web host at port 8090, alongside the existing `/metrics` listener on 8085.

**Port mappings (compose):**

| Service | Port |
|---|---|
| PostgreSQL | 5432 |
| RabbitMQ AMQP | 5672 |
| RabbitMQ Management UI | 15672 |
| Redis | 6379 |
| Worker `/metrics` (Prometheus) | 8085 |
| Worker `/health` | 8090 |

---

## CI/CD

`.editorconfig` and `Directory.Build.props` enable `AnalysisLevel=latest`/`AnalysisMode=Recommended` with `TreatWarningsAsErrors`. `Directory.Build.props` documents the specific pre-existing warning rule IDs grandfathered in — nullable-safety warnings are not among them and fail the build if introduced.

This repo has its own GitHub Actions, independent of the monorepo root's Actions tab (it's a separate git remote — see the monorepo root's CI/CD section for how the two relate). Two workflows live under `.github/workflows/`: `ci.yml` and `deploy.yml` — there's no `load-test.yml` here, since this service has no public HTTP API to load-test.

### `ci.yml` — build, test, quality gate

Runs on every push/PR to `main`:

1. Checks out this repo into `ArbiScanner.TelegramNotifierApp/` plus both sibling repos — `ArbiScannerWebApp` into `ArbiScannerWebApp/` and `ArbiScannerAdminPannel` into `ArbiScannerAdminPannel/` — since this `.slnx` references project files from both directly (`ArbiScannerAdminPanel.Infrastructure`, `ArbiScannerWeb.Abstractions`, `ArbiScannerWeb.Domain` — see [Docker Build](#docker-build) and [Project Layer Responsibilities](#project-layer-responsibilities)).
2. A SonarCloud scan (project `dimasdom_ArbiSpreadScanner.TelegramNotifierApp`) wraps everything below, excluding both sibling checkouts from analysis (`**/ArbiScannerWebApp/**`, `**/ArbiScannerAdminPannel/**` — each is scanned by its own repo's CI); `sonar.qualitygate.wait=true` fails the job on a red quality gate.
3. CodeQL (`build-mode: manual`, `source-root: ArbiScanner.TelegramNotifierApp`) analyzes only this repo's C# code, not the sibling checkouts.
4. `dotnet restore`/`build` on `ArbiScanner.slnx` with analyzers, then `ArbiScanner.TelegramNotifierApp.Tests` (unit) with coverage collection feeding the SonarCloud scan. There's no integration test project for this service.
5. `.trx` results are published as a check-run summary via `dorny/test-reporter`.

Both SonarCloud and CodeQL are free for this public repo; SonarCloud additionally requires a `SONAR_TOKEN` secret.

### `deploy.yml` — manual deploy to the VPS

A `workflow_dispatch`-triggered workflow (optional `dry_run` boolean input) that calls the monorepo root's reusable `deploy-service.yml` (`dimasdom/SpreadScanner/.github/workflows/deploy-service.yml`, pinned to a specific commit SHA) with this repo's specifics: solution file, unit test project, an empty `integration_test_project` (none exists), the SonarCloud exclusion list, `sibling_repos` set to check out both `ArbiScannerWebApp` and `ArbiScannerAdminPannel` (needed by the Docker build — see [Docker Build](#docker-build)), and a single image spec (`arbiscanner-telegram-notifier`, build context `.`, repo root).

End to end: unit tests + quality gate → build and push `ghcr.io/dimasdom/arbiscanner-telegram-notifier:latest` / `:sha-<commit>` to GHCR → (unless `dry_run: true`) SSH into the VPS and restart `telegram-notifier` via `scripts/deploy-remote.sh`. Requires `SONAR_TOKEN` plus `VPS_HOST`/`VPS_USER`/`VPS_SSH_KEY`/`VPS_SSH_PORT`/`VPS_DEPLOY_PATH` secrets on this repo.

The root monorepo also has `.github/workflows/docker-build.yml`, since this Worker's Dockerfile needs repo-root build context — it builds this service's image alongside the other three on every push/PR to `master`, as a build-breakage smoke check separate from this repo's own CI.

---

## Testing

`ArbiScanner.TelegramNotifierApp.Tests` is a unit test project (xUnit + NSubstitute + `Microsoft.EntityFrameworkCore.InMemory`) covering the Application layer:

| File | Coverage |
|---|---|
| `SpreadServiceTests` | Spread percentage calculation and the Spot/Funding/Futures message constructors — including that a funding-rate line is included/omitted correctly (this is what would have caught a stale risk-level string left in a message template) |
| `TelegramNotifierUserServiceTests` | `NotifyUser` against a substituted `ITelegramBotClient` — valid/empty/whitespace messages and bot-client exceptions all log as expected |
| `TelegramUserServiceTests` | Link/unlink/active-status flows against a real EF Core `AppDbContext` backed by the in-memory provider (valid link, invalid link ID, link with no matching `UserSettings` row, unlinking a user that doesn't exist, etc.) |

```bash
cd ArbiScanner.TelegramNotifierApp
dotnet test ArbiScanner.TelegramNotifierApp.Tests/ArbiScanner.TelegramNotifierApp.Tests.csproj
```

---

## Project Structure

```
ArbiScanner.TelegramNotifierApp/
├── ArbiScanner.TelegramNotifierApp.Domain/
│   └── Settings/
│       └── TelegramSettings.cs          # Bot token configuration model
│
├── ArbiScanner.TelegramNotifierApp.Abstractions/
│   └── Interfaces/Services/
│       ├── ISpreadService.cs            # HandleNewSpread / HandleCloseSpread
│       ├── ITelegramNotifierUserService.cs  # NotifyUser
│       └── ITelegramUserService.cs      # Link / Unlink / UpdateActiveStatus
│
├── ArbiScanner.TelegramNotifierApp.Application/
│   └── Services/
│       ├── SpreadService.cs             # Filters users, formats messages, dispatches
│       ├── TelegramNotifierUserService.cs  # Wraps ITelegramBotClient.SendMessage
│       └── TelegramUserService.cs       # Account linking/unlinking logic
│
├── ArbiScanner.TelegramNotifierApp.Infrastructure/
│   └── DbContext/
│       └── AppDbContext.cs              # EF Core DbContext (PostgreSQL)
│
├── ArbiScanner.TelegramNotifierApp.Worker/
│   ├── Worker/
│   │   ├── MessageBroker/
│   │   │   └── SpreadsMessageBroker.cs  # RabbitMQ consumer BackgroundService
│   │   └── TelegramMessageController/
│   │       ├── TelegramMessageController.cs  # Bot polling BackgroundService
│   │       └── TelegramMainController.cs     # Update router and command handlers
│   ├── Program.cs                       # Host setup, DI registration
│   ├── appsettings.json
│   └── appsettings.Development.json
│
├── ArbiScanner.TelegramNotifierApp.Tests/  # Unit tests — see Testing
│   ├── SpreadServiceTests.cs
│   ├── TelegramNotifierUserServiceTests.cs
│   ├── TelegramUserServiceTests.cs
│   └── TestHelpers.cs
│
├── ArbiScanner.slnx                     # Solution file
├── Dockerfile                           # Multi-stage build (context: repo root)
└── docker-compose.yml                   # Worker + PostgreSQL + RabbitMQ stack
```

### Cross-project dependencies

```
Worker
  -> Application  -> Abstractions -> ArbiScannerWeb.Domain
  -> Infrastructure
  -> ArbiScannerAdminPanel.Infrastructure  (shared DbContext/models)
  -> ArbiScannerWeb.Abstractions
  -> ArbiScannerWeb.Domain
```

The `ArbiScannerBot` PostgreSQL database is shared between this service and the ArbiScanner web application. User accounts, exchange preferences, and Telegram link records all reside in that database. The web application manages user registration and generates link codes; this service consumes those records to route notifications.
