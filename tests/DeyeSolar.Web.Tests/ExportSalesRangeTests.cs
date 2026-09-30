using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Services;

namespace DeyeSolar.Web.Tests;

public class ExportSalesRangeTests
{
    private static readonly SolarSalesOptions Options = new() { ContractStartDate = new(2024, 1, 1) };
    private static readonly DateTimeOffset Now = new(2026, 12, 31, 20, 39, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2026, 3, 29, 23)]
    [InlineData(2026, 10, 25, 25)]
    public void WarsawDstDaysContainEveryDistinctUtcHour(int year, int month, int day, int count)
    {
        var request = new ExportSalesRequest(ExportSalesPeriod.Day, new(year, month, day));
        var range = ExportSalesRange.Create(request, Options, Now);
        var buckets = ExportSalesRange.Buckets(request, range.Start, range.End, Options.TimeZoneId);
        Assert.Equal(count, buckets.Count);
        Assert.Equal(count, (range.End - range.Start).TotalHours);
        Assert.Equal(count, buckets.Select(bucket => bucket.Start).Distinct().Count());
        Assert.All(buckets, bucket => Assert.Equal(TimeSpan.FromHours(1), bucket.End - bucket.Start));
    }

    [Fact]
    public void CalendarMonthAndYearUseLocalBoundariesAndIncludeLeapDay()
    {
        var month = new ExportSalesRequest(ExportSalesPeriod.Month, new(2024, 2, 17));
        var range = ExportSalesRange.Create(month, Options, Now);
        Assert.Equal(new DateTimeOffset(2024, 1, 31, 23, 0, 0, TimeSpan.Zero), range.Start);
        Assert.Equal(29, ExportSalesRange.Buckets(month, range.Start, range.End, Options.TimeZoneId).Count);
        var year = new ExportSalesRequest(ExportSalesPeriod.Year, new(2024, 12, 30));
        range = ExportSalesRange.Create(year, Options, Now);
        Assert.Equal(366 * 24, (range.End - range.Start).TotalHours);
        Assert.Equal(12, ExportSalesRange.Buckets(year, range.Start, range.End, Options.TimeZoneId).Count);
    }

    [Fact]
    public void CurrentPeriodStopsAtLastCompletedHourAndContractStart()
    {
        var config = new SolarSalesOptions();
        var now = new DateTimeOffset(2026, 9, 30, 10, 37, 0, TimeSpan.Zero);
        var range = ExportSalesRange.Create(new(ExportSalesPeriod.Month, new(2026, 9, 30)), config, now);
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 22, 0, 0, TimeSpan.Zero), range.DataStart);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero), range.DataEnd);
        Assert.Equal(new DateOnly(2026, 9, 30), range.Today);
        Assert.Equal(60, (range.DataEnd - range.DataStart).TotalHours);
        var old = ExportSalesRange.Create(new(ExportSalesPeriod.Day, new(2026, 9, 27)), config, now);
        Assert.True(old.DataEnd <= old.DataStart);
    }

    [Fact]
    public void InclusiveCustomDatesPreserveTwentyFiveHourDay()
    {
        var request = new ExportSalesRequest(ExportSalesPeriod.Custom, new(2026, 10, 25), new(2026, 10, 24), new(2026, 10, 25));
        var range = ExportSalesRange.Create(request, Options, Now);
        Assert.Equal(49, (range.End - range.Start).TotalHours);
        var buckets = ExportSalesRange.Buckets(request, range.Start, range.End, Options.TimeZoneId);
        Assert.Equal(new[] { 24d, 25d }, buckets.Select(bucket => (bucket.End - bucket.Start).TotalHours));
    }

    [Fact]
    public void InvalidOrExcessiveCalendarRangesFailBeforeReadingData()
    {
        Assert.Throws<ArgumentException>(() => ExportSalesRange.Create(new(ExportSalesPeriod.Custom, new(2026, 1, 1)), Options, Now));
        Assert.Throws<ArgumentException>(() => ExportSalesRange.Create(new(ExportSalesPeriod.Custom, new(2026, 1, 1), new(2026, 1, 2), new(2026, 1, 1)), Options, Now));
        Assert.Throws<ArgumentException>(() => ExportSalesRange.Create(new(ExportSalesPeriod.Custom, new(2026, 1, 1), new(2025, 1, 1), new(2026, 1, 2)), Options, Now));
        Assert.Throws<ArgumentException>(() => ExportSalesRange.Create(new(ExportSalesPeriod.Day, new(2027, 1, 1)), Options, Now));
        Assert.Throws<ArgumentException>(() => ExportSalesRange.Create(new(ExportSalesPeriod.Day, new(1999, 12, 31)), Options, Now));
        Assert.Throws<ArgumentException>(() => ExportSalesRange.Create(new(ExportSalesPeriod.Custom, new(2026, 12, 31), new(2026, 12, 31), new(2027, 1, 1)), Options, Now));
    }
}
