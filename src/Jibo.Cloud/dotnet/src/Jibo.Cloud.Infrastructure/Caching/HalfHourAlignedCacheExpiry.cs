namespace Jibo.Cloud.Infrastructure.Caching;

/// <summary>
/// Expires cache entries at the next clock half-hour (:00 or :30), so a hit at
/// 2:23 lasts until 2:30 and a hit at 2:34 lasts until 3:00.
/// </summary>
public static class HalfHourAlignedCacheExpiry
{
    /// <summary>
    /// Returns the next UTC :00 or :30 strictly after <paramref name="utcNow"/>.
    /// If <paramref name="utcNow"/> is already on a boundary, jumps forward 30 minutes.
    /// </summary>
    public static DateTimeOffset GetExpiryUtc(DateTimeOffset utcNow)
    {
        var utc = utcNow.ToUniversalTime();
        var hourStart = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
        var halfHour = hourStart.AddMinutes(30);
        var nextHour = hourStart.AddHours(1);

        if (utc < halfHour)
            return halfHour;

        return nextHour;
    }
}
