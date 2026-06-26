// SPDX-License-Identifier: MIT
using System.Text.Json.Serialization;

namespace NotificationFunction
{
    public class NotificationStateEntity
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("lastNotificationTime")]
        public string LastNotificationTime { get; set; } = string.Empty;
    }
}
