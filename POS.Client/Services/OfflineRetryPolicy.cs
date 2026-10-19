using System;

namespace POS.Client.Services
{
    public static class OfflineRetryPolicy
    {
        private static readonly Random _jitter = new();

        public static TimeSpan CalculateNextRetryDelay(int retryCount)
        {
            // Exponential backoff with jitter
            // 1st retry: ~5s
            // 2nd retry: ~15s
            // 3rd retry: ~30s
            // 4th+ retry: capped at 5 minutes (300s)

            double baseSeconds = retryCount switch
            {
                1 => 5.0,
                2 => 15.0,
                3 => 30.0,
                _ => 300.0
            };

            // Add up to 20% random jitter to prevent retry storms
            double jitterFactor = 1.0 + (_jitter.NextDouble() * 0.4 - 0.2);
            double totalSeconds = Math.Min(300.0, baseSeconds * jitterFactor);

            return TimeSpan.FromSeconds(totalSeconds);
        }

        public static bool IsRetryableStatusCode(int statusCode)
        {
            return statusCode == 429 || statusCode >= 500;
        }

        public static bool IsPermanentFailureStatusCode(int statusCode)
        {
            return statusCode is 400 or 401 or 403 or 409 or 422;
        }
    }
}
