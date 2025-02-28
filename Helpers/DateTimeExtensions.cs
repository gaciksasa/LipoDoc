using System;

namespace DeviceDataCollector.Helpers
{
    public static class DateTimeExtensions
    {
        /// <summary>
        /// Formats a DateTime for display with a standard format
        /// </summary>
        public static string ToDisplayFormat(this DateTime dateTime, string format = "yyyy-MM-dd HH:mm:ss")
        {
            return dateTime.ToString(format);
        }

        /// <summary>
        /// Formats a nullable DateTime for display with a standard format
        /// </summary>
        public static string ToDisplayFormat(this DateTime? dateTime, string format = "yyyy-MM-dd HH:mm:ss", string nullValue = "Never")
        {
            return dateTime.HasValue ? dateTime.Value.ToString(format) : nullValue;
        }
    }
}