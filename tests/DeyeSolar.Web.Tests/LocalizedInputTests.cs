using System.Globalization;
using DeyeSolar.Web.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace DeyeSolar.Web.Tests;

public class LocalizedInputTests
{
    [Fact]
    public void InputsKeepIndependentConversionErrorsAndReuseTheirOwnState()
    {
        using var services = Services();
        var text = services.GetRequiredService<UiText>();
        var owner = new object();
        var first = text.InputConverter<int>(owner, "first");
        var second = text.InputConverter<int>(owner, "second");

        first.Get("invalid");
        Assert.True(first.GetError);
        Assert.Equal(80, second.Get("80"));
        Assert.False(second.GetError);
        Assert.True(first.GetError);
        Assert.Same(first, text.InputConverter<int>(owner, "first"));
        Assert.Equal(90, first.Get("90"));
        Assert.False(first.GetError);
    }

    [Fact]
    public void NumericInputPreservesSelectedCultureForParsingAndDisplay()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            using var services = Services();
            var converter = services.GetRequiredService<UiText>().InputConverter<double>(new object(), "capacity");
            Assert.Equal(12.5, converter.Get("12,5"));
            Assert.False(converter.GetError);
            Assert.Equal("12,5", converter.Set(12.5));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void TimeInputPreservesHourMinuteFormatAndEmptyOptionalValue()
    {
        using var services = Services();
        var converter = services.GetRequiredService<UiText>().InputConverter<TimeSpan?>(new object(), "active-from", "HH:mm");
        Assert.Equal("17:30", converter.Set(new TimeSpan(17, 30, 0)));
        Assert.Equal(new TimeSpan(17, 30, 0), converter.Get("17:30"));
        Assert.Null(converter.Get(""));
        Assert.False(converter.GetError);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddComponentLocalization();
        return services.BuildServiceProvider();
    }
}
