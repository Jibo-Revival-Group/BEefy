using Jibo.Cloud.Infrastructure.Calendar;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class IcalCalendarParserTests
{
    [Fact]
    public void ParseEventsForWindow_ReadsTimedAllDayAndWeeklyEvents()
    {
        var today = new DateOnly(2026, 7, 16);
        var tomorrow = today.AddDays(1);
        // Thursday 2026-07-16
        var ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            DTSTART:20260716T150000Z
            DTEND:20260716T160000Z
            SUMMARY:Team sync
            END:VEVENT
            BEGIN:VEVENT
            DTSTART;VALUE=DATE:20260717
            DTEND;VALUE=DATE:20260718
            SUMMARY:Company holiday
            END:VEVENT
            BEGIN:VEVENT
            DTSTART:20260709T180000Z
            RRULE:FREQ=WEEKLY;BYDAY=TH
            SUMMARY:Weekly standup
            END:VEVENT
            END:VCALENDAR
            """;

        var events = IcalCalendarParser.ParseEventsForWindow(ics, today, tomorrow, TimeZoneInfo.Utc);

        Assert.Contains(events, item =>
            item.Date == today &&
            item.Summary == "Team sync" &&
            !item.IsAllDay);
        Assert.Contains(events, item =>
            item.Date == tomorrow &&
            item.Summary == "Company holiday" &&
            item.IsAllDay &&
            item.TimeLabel == "all day");
        Assert.Contains(events, item =>
            item.Date == today &&
            item.Summary == "Weekly standup");
    }

    [Theory]
    // Thursday 2026-07-16 / Friday 2026-07-17
    [InlineData("DTSTART:20260706T130000Z", "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", true, true)]   // every weekday, started on a Monday
    [InlineData("DTSTART:20260706T130000Z", "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,TH", false, false)] // every other week: not this week
    [InlineData("DTSTART:20260702T130000Z", "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,TH", true, false)]  // every other week from the 2nd: this Thursday
    [InlineData("DTSTART:20200101T130000Z", "FREQ=DAILY", true, true)]                             // started years ago
    [InlineData("DTSTART:20200101T130000Z", "FREQ=DAILY;INTERVAL=2", true, false)]                 // every other day from 2020-01-01 (2,388 days before)
    [InlineData("DTSTART:20200101T130000Z", "FREQ=DAILY;COUNT=10", false, false)]                  // ended long ago
    [InlineData("DTSTART:20200102T130000Z", "FREQ=WEEKLY", true, false)]                           // every Thursday since 2020
    public void ParseEventsForWindow_ExpandsRepeats(string start, string rrule, bool today, bool tomorrow)
    {
        var day = new DateOnly(2026, 7, 16);
        var ics = $"BEGIN:VCALENDAR\nBEGIN:VEVENT\n{start}\nRRULE:{rrule}\nSUMMARY:Repeat\nEND:VEVENT\nEND:VCALENDAR\n";

        var events = IcalCalendarParser.ParseEventsForWindow(ics, day, day.AddDays(1), TimeZoneInfo.Utc);

        Assert.Equal(today, events.Any(item => item.Date == day));
        Assert.Equal(tomorrow, events.Any(item => item.Date == day.AddDays(1)));
    }
}
