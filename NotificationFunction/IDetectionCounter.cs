// SPDX-License-Identifier: MIT
using System.Threading.Tasks;

namespace NotificationFunction
{
    public interface IDetectionCounter
    {
        Task<int> CountRecentAsync(string locationId, int periodMinutes);
    }
}
