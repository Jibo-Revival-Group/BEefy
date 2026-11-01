using Jibo.Cloud.Infrastructure.Calendar;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class IcalUrlValidatorTests
{
    [Fact]
    public void TryValidateHttpsPublicUrl_RejectsHttpAndLoopback()
    {
        Assert.False(IcalUrlValidator.TryValidateHttpsPublicUrl(
            "http://calendar.google.com/calendar/ical/demo/basic.ics",
            out _,
            out var httpError));
        Assert.Contains("https", httpError, StringComparison.OrdinalIgnoreCase);

        Assert.False(IcalUrlValidator.TryValidateHttpsPublicUrl(
            "https://127.0.0.1/calendar.ics",
            out _,
            out var loopbackError));
        Assert.Contains("not allowed", loopbackError, StringComparison.OrdinalIgnoreCase);

        Assert.False(IcalUrlValidator.TryValidateHttpsPublicUrl(
            "https://localhost/calendar.ics",
            out _,
            out var localhostError));
        Assert.Contains("not allowed", localhostError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PhoneCalendar_IsAcceptedAndReadWithoutFetching()
    {
        const string ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:Dentist\r\nDTSTART:20261102T140000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var phone = IcalUrlValidator.PhoneCalendarPrefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ics));

        Assert.True(IcalUrlValidator.TryValidateHttpsPublicUrl(phone, out _, out _));
        Assert.Equal("iPhone", IcalUrlValidator.TryGetSafeHost(phone));
        // The HttpClient has no handler for any address, so a fetch attempt would throw.
        var fetched = await new IcalFeedFetcher(new HttpClient(new ThrowingHandler()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IcalFeedFetcher>.Instance).FetchAsync(phone);
        Assert.Equal(ics, fetched.Body);

        Assert.False(IcalUrlValidator.TryValidateHttpsPublicUrl(IcalUrlValidator.PhoneCalendarPrefix + "bm90IGEgY2FsZW5kYXI=", out _, out var notCalendar));
        Assert.Contains("isn't a calendar", notCalendar);
        Assert.False(IcalUrlValidator.TryValidateHttpsPublicUrl(phone + new string('A', IcalUrlValidator.MaxPhoneCalendarLength), out _, out _));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Phone calendars must not be fetched.");
    }

    [Fact]
    public void TryGetSafeHost_ReturnsHostOnly()
    {
        Assert.Equal(
            "calendar.google.com",
            IcalUrlValidator.TryGetSafeHost("https://calendar.google.com/calendar/ical/demo/private-token/basic.ics"));
    }

    [Fact]
    public void TryValidateHttpsPublicUrl_AllowsHostnameWithoutDnsOnSavePath()
    {
        Assert.True(IcalUrlValidator.TryValidateHttpsPublicUrl(
            "https://calendar.example.com/ical/private-token/basic.ics",
            out var uri,
            out var error,
            requireDnsResolution: false));
        Assert.Null(error);
        Assert.Equal("calendar.example.com", uri.Host);
    }
}
