using System;

namespace UnsecuredAPIKeys.Services
{
    public static class DateTimeExtensions
    {
        /// <summary>
        /// Converts a UTC DateTime to Indian Standard Time (UTC+5:30).
        /// </summary>
        public static DateTime ToIst(this DateTime utcDateTime)
        {
            // IST is fixed at UTC+5:30
            // Using a fixed offset is safer for cross-platform (Windows/Linux) than TimeZoneInfo IDs.
            return utcDateTime.AddHours(5).AddMinutes(30);
        }

        /// <summary>
        /// Converts a nullable UTC DateTime to Indian Standard Time (UTC+5:30).
        /// </summary>
        public static DateTime? ToIst(this DateTime? utcDateTime)
        {
            if (!utcDateTime.HasValue) return null;
            return utcDateTime.Value.ToIst();
        }

        /// <summary>
        /// Returns a formatted IST string.
        /// </summary>
        public static string ToIstString(this DateTime utcDateTime, string format = "yyyy-MM-dd HH:mm:ss")
        {
            return utcDateTime.ToIst().ToString(format) + " IST";
        }
    }
}
