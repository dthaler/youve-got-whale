using Azure;
using Azure.Data.Tables;
using System;

namespace NotificationFunction
{
    public class NotificationStateEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = "NotificationState";
        public string RowKey { get; set; } = string.Empty;
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public DateTime LastNotificationTime { get; set; }
    }
}
