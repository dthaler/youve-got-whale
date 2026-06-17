# GitHub Copilot Instructions

## Project Overview

This repository contains **You've Got Whale**, an Azure Functions app written in C# (.NET 8) that sends email alerts when whale detections cluster at a configured hydrophone node. It integrates with Cosmos DB for detection data and AWS SES for email delivery.

## Architecture

- **Runtime**: Azure Functions v4, .NET 8 isolated worker
- **Trigger**: Cosmos DB change feed via `CosmosDBTrigger`
- **Notifications**: AWS SES (Simple Email Service)
- **State**: Azure Table Storage (tracks last notification time per node)
- **Detection source**: Cosmos DB container (`detections` database, `metadata` container)

## Coding Conventions

- Use C# with nullable reference types enabled (`<Nullable>enable</Nullable>`)
- Implicit usings are disabled; add explicit `using` directives
- Follow existing patterns in `NotificationFunction/` for Azure Functions bindings and dependency injection
- Keep configuration values in `local.settings.json` (local) and Azure Function App settings (production); never hard-code secrets
- Use `ILogger<T>` for logging

## Build & Test

```sh
cd NotificationFunction
dotnet build
```

## Configuration

See `README.md` for the full list of required and optional environment variables.
