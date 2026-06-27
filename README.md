# You've Got Whale

Azure Functions app that sends app notifications when whale detections cluster at a configured hydrophone node.

## Configuration

Copy `NotificationFunction/local.settings.json.example` to `NotificationFunction/local.settings.json` for local development and set these values:

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `AzureWebJobsStorage` | Yes | — | Azure Functions storage connection |
| `FUNCTIONS_WORKER_RUNTIME` | Yes | `dotnet-isolated` | Azure Functions worker runtime |
| `CosmosDbConnection` | Yes | — | Cosmos DB connection string |
| `CosmosDbDatabase` | No | `detections` | Cosmos DB database name |
| `CosmosDbContainer` | No | `metadata` | Cosmos DB container name |
| `StorageConnection` | Yes | — | Azure Table Storage connection string |
| `AppNotificationUrl` | Yes | — | IFTTT webhook URL for notifications |
| `LocationId` | Yes | — | Hydrophone location name to monitor |
| `NotificationPeriodMinutes` | No | `60` | Minimum time between notifications |
| `DetectionPeriodMinutes` | No | `15` | Detection lookback window |

## Build

```sh
cd NotificationFunction
dotnet build
```

## Test

```sh
dotnet test NotificationFunction.sln
```
