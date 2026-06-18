// SPDX-License-Identifier: MIT
using System;
using System.Threading.Tasks;

namespace NotificationFunction
{
    public interface INotificationStateStore
    {
        Task<DateTime?> GetLastNotificationTimeAsync(string locationId);
        Task UpdateLastNotificationTimeAsync(string locationId);
    }
}
