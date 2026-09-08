using System;
using System.Threading.Tasks;
using ValleytalkReborn.Platform;

namespace ValleytalkReborn
{
    /// <summary>
    /// Helper class for network availability checking in patches
    /// </summary>
    public static class NetworkAvailabilityChecker
    {
        public static async Task<bool> IsNetworkAvailableWithRetryAsync()
        {
            if (!AndroidHelper.IsAndroid)
                return true;

            if (NetworkHelper.IsNetworkAvailable())
                return true;

            ModEntry.SMonitor?.Log("Network not available, retrying once per second for 5 seconds...", StardewModdingAPI.LogLevel.Warn);
            
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(1000);
                
                if (NetworkHelper.IsNetworkAvailable())
                    return true;
            }

            ModEntry.SMonitor?.Log("Network still not available after retrying for 5 seconds, disabling AI dialogue generation", StardewModdingAPI.LogLevel.Warn);
            return false;
        }

        /// <summary>
        /// Synchronous version that blocks for the network check.
        /// 【Bug 修复】使用 Task.Run 隔离同步上下文，防止主线程调用 .Result 导致游戏界面死锁卡死
        /// </summary>
        public static bool IsNetworkAvailableWithRetry()
        {
            if (!AndroidHelper.IsAndroid)
                return true;

            try
            {
                return Task.Run(async () => await IsNetworkAvailableWithRetryAsync()).GetAwaiter().GetResult();
            }
            catch
            {
                return false;
            }
        }
    }
}