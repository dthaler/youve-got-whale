# You've Got Whale

Azure Functions app that sends email alerts when whale detections cluster at a configured hydrophone node.

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
