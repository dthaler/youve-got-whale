# You've Got Whale

Azure Functions app that sends email alerts when whale detections cluster at a configured hydrophone node.

The detections container is treated as read-only. Notification rate-limit state is stored in a separate writable Cosmos DB container.

## Configuration

Copy `NotificationFunction/local.settings.json.example` to `NotificationFunction/local.settings.json` for local development and set these values:

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `AzureWebJobsStorage` | Yes | — | Azure Functions storage connection |
| `FUNCTIONS_WORKER_RUNTIME` | Yes | `dotnet-isolated` | Azure Functions worker runtime |
| `CosmosDbConnection` | Yes | — | Read-only detections Cosmos DB connection string |
| `CosmosDbDatabase` | No | `predictions` | Cosmos DB database name |
| `CosmosDbContainer` | No | `metadata` | Cosmos DB container name |
| `NotificationCosmosDbConnection` | Yes | — | Read-write notifications Cosmos DB connection string |
| `NotificationCosmosDbDatabase` | No | `orcasound-cosmosdb` | Notifications Cosmos DB database name |
| `NotificationCosmosDbContainer` | No | `Notifications` | Notifications Cosmos DB container name |
| `SenderEmail` | Yes | — | AWS SES sender address |
| `AwsRegion` | No | `us-west-2` | AWS region for SES |
| `LocationId` | Yes | — | Hydrophone location ID to monitor |
| `RecipientEmail` | Yes | — | Notification recipient |
| `NotificationPeriodMinutes` | No | `60` | Minimum time between notifications |
| `DetectionPeriodMinutes` | No | `15` | Detection lookback window |

## Build

```sh
cd NotificationFunction
dotnet build
```
