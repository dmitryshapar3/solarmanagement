using System.Globalization;
using DeyeSolar.Web.Components.Ui;
namespace DeyeSolar.Web.Tests;
public class LocalizedInputTests
{
    [Theory]
    [InlineData("pl-PL", "12,5", 12.5)]
    [InlineData("en-GB", "12.5", 12.5)]
    public void NativeNumberInputsUseTheSelectedCultureAndRejectNonFiniteValues(string culture, string input, double expected)
    {
        var format = CultureInfo.GetCultureInfo(culture);
        Assert.True(UiInputParser.TryNumber(input, format, out var parsed)); Assert.Equal(expected, parsed);
        foreach (var invalid in new[] { "invalid", "", "NaN", "Infinity" }) Assert.False(UiInputParser.TryNumber(invalid, format, out _));
    }
    [Fact]
    public void NativeTimeInputPreservesHourMinuteAndEmptyOptionalValue()
    {
        Assert.True(UiInputParser.TryTime("17:30", out var time)); Assert.Equal(new TimeSpan(17, 30, 0), time);
        Assert.True(UiInputParser.TryTime("", out time)); Assert.Null(time);
        foreach (var invalid in new[] { "24:00", "17:60", "09:12:13", "invalid" }) Assert.False(UiInputParser.TryTime(invalid, out _));
    }
}
