using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Content;

namespace Jibo.Cloud.Tests.Application;

public sealed class ReactiveHolidayReplyTests
{
    [Theory]
    [InlineData("holidayClaim===\"Christmas\" && _now.isInRange('1/1', '11/30')", "2026-07-27", true)]
    [InlineData("holiday===\"Christmas\"", "2026-12-25", true)]
    [InlineData("holidayClaim===\"Christmas\" && _now.isInRange('12/1', '12/31')", "2026-12-20", true)]
    public void LegacyMimConditionEvaluator_MatchesHolidayConditions(string condition, string dateText, bool expected)
    {
        var context = new LegacyMimConditionEvaluator.Context(
            "Christmas",
            "Christmas",
            DateOnly.Parse(dateText));

        Assert.Equal(expected, LegacyMimConditionEvaluator.Matches(condition, context));
    }

    [Fact]
    public void JiboHolidayGreeting_ExtractsChristmasClaim()
    {
        Assert.True(JiboHolidayGreeting.TryExtractHolidayClaim("merry christmas", out var claim));
        Assert.Equal("Christmas", claim);
    }

    [Theory]
    [InlineData("mary christmas", "Christmas")]
    [InlineData("Mary Christmas!", "Christmas")]
    [InlineData("hey jibo mary christmas", "Christmas")]
    [InlineData("mary christmas to you too", "Christmas")]
    [InlineData("mary christmas eve", "Christmas Eve")]
    public void ObservedMaryChristmasHomophoneRetainsHolidayClaim(string transcript, string claim)
    {
        Assert.True(JiboHolidayGreeting.TryExtractHolidayClaim(transcript, out var actual));
        Assert.Equal(claim, actual);
    }

    [Theory]
    [InlineData("who is mary christmas")]
    [InlineData("my name is mary christmas")]
    [InlineData("mary likes christmas")]
    [InlineData("tell me about mary christmas")]
    public void MaryChristmasNamesAndQuestionsAreNotHolidayGreetings(string transcript)
    {
        Assert.False(JiboHolidayGreeting.TryExtractHolidayClaim(transcript, out _));
    }

    [Theory]
    [InlineData("happy thanksgiving", "Thanksgiving")]
    [InlineData("merry christmas", "Christmas")]
    [InlineData("have a happy easter", "Easter")]
    [InlineData("happy holidays", null)]
    public void JiboHolidayGreeting_ExtractsReactiveHolidayClaims(string transcript, string? expectedClaim)
    {
        Assert.True(JiboHolidayGreeting.TryExtractHolidayClaim(transcript, out var claim));
        Assert.Equal(expectedClaim, claim);
    }

    [Theory]
    [InlineData("how is thanksgiving")]
    [InlineData("do you like thanksgiving")]
    public void JiboHolidayGreeting_DoesNotTreatHolidayQuestionsAsGreetings(string transcript)
    {
        Assert.False(JiboHolidayGreeting.TryExtractHolidayClaim(transcript, out _));
    }

    [Theory]
    [InlineData("Christmas", "Christmas Day")]
    [InlineData("Thanksgiving", "Thanksgiving Day")]
    public void JiboHolidayGreeting_IsClaimedHolidayToday_MatchesNagerStyleNames(
        string holidayClaim,
        string calendarName)
    {
        Assert.True(JiboHolidayGreeting.IsClaimedHolidayToday(holidayClaim, [calendarName]));
    }

    [Fact]
    public async Task ReactiveHolidayReplyBuilder_UsesImportedChristmasLinesFromMergedCatalog()
    {
        var catalog = await new InMemoryJiboExperienceContentRepository().GetCatalogAsync();
        var randomizer = new FirstReplyRandomizer();

        var julyDecision = ReactiveHolidayReplyBuilder.BuildDecision(
            catalog,
            randomizer,
            "merry christmas",
            DateTimeOffset.Parse("2026-07-27T12:00:00-04:00"),
            [],
            "seasonal_holiday_greeting");

        Assert.Equal("NotHoliday", julyDecision.ContextUpdates!["chitchatRoute"]);
        Assert.Contains("Christmastime", julyDecision.ReplyText, StringComparison.OrdinalIgnoreCase);

        var christmasDecision = ReactiveHolidayReplyBuilder.BuildDecision(
            catalog,
            randomizer,
            "merry christmas",
            DateTimeOffset.Parse("2026-12-25T12:00:00-05:00"),
            ["Christmas"],
            "seasonal_holiday_greeting");

        Assert.Equal("HolidayResponse", christmasDecision.ContextUpdates!["chitchatRoute"]);
        Assert.Contains("Merry Christmas", christmasDecision.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FirstReplyRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];

        public double NextUnitInterval() => 0.0;
    }
}
