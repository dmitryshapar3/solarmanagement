using System.Net;
using System.Text;
using System.Xml;
using DeyeSolar.Domain.Options;
using DeyeSolar.Infrastructure.Settlement;

namespace DeyeSolar.Infrastructure.Tests;

public sealed class ExportPriceFeedClientTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-09T00:00:00Z");
    private const string Header = "interval_start,interval_end,price_pln_per_kwh\n";
    [Fact]
    public void HourlyCsvNormalizesOffsetAndExpandsWithoutChangingPriceOrMissingValues()
    {
        var rows = ExportPriceFeedClient.Parse(Header + "2026-10-09T02:00:00+02:00,2026-10-09T03:00:00+02:00,0.345678\n"
            + "2026-10-09T03:00:00+02:00,2026-10-09T04:00:00+02:00,\n", Start, Start.AddHours(2));
        Assert.Equal(4, rows.Count); Assert.Equal(Start, rows[0].Start);
        Assert.All(rows, row => Assert.Equal(345.678m, row.PricePlnPerMwh));
        Assert.Equal(Start.AddHours(1), rows[^1].End);
    }
    [Fact]
    public void XmlQuarterPricesPreserveExplicitSignedMwhAndZero()
    {
        var rows = ExportPriceFeedClient.Parse("<prices><price><interval_start>2026-10-09T00:00:00Z</interval_start><interval_end>2026-10-09T00:15:00Z</interval_end><price_pln_per_mwh>-17.123456</price_pln_per_mwh></price><price><interval_start>2026-10-09T00:15:00Z</interval_start><interval_end>2026-10-09T00:30:00Z</interval_end><price_pln_per_mwh>0</price_pln_per_mwh></price></prices>", Start, Start.AddHours(1));
        Assert.Equal(new[] { -17.123456m, 0m }, rows.Select(p => p.PricePlnPerMwh));
    }
    [Theory]
    [InlineData("interval_start,interval_end,price\n2026-10-09T00:00:00Z,2026-10-09T01:00:00Z,1")]
    [InlineData("interval_start,interval_end,price_pln_per_mwh\n2026-10-09T00:00:00,2026-10-09T01:00:00,1")]
    [InlineData("interval_start,interval_end,price_pln_per_kwh\n2026-10-09T00:01:00Z,2026-10-09T01:01:00Z,1")]
    [InlineData("interval_start,interval_end,price_pln_per_kwh\n2026-10-09T00:00:00Z,2026-10-09T00:30:00Z,1")]
    [InlineData("interval_start,interval_end,price_pln_per_kwh\n2026-10-09T00:00:00Z,2026-10-09T01:00:00Z,NaN")]
    [InlineData("interval_start,interval_end,price_pln_per_mwh\n2026-10-09T00:00:00Z,2026-10-09T01:00:00Z,0.1234567")]
    [InlineData("interval_start,interval_end,price_pln_per_mwh,price_pln_per_kwh\n2026-10-09T00:00:00Z,2026-10-09T01:00:00Z,1,1")]
    public void AmbiguousUnitsTimesDurationsAndAmountsAreRejected(string text) => Assert.Throws<InvalidDataException>(() => ExportPriceFeedClient.Parse(text, Start, Start.AddHours(2)));
    [Fact]
    public void OverlapCannotSilentlyReplaceAnInterval()
    {
        var row = "2026-10-09T00:00:00Z,2026-10-09T01:00:00Z,1\n";
        Assert.Throws<InvalidDataException>(() => ExportPriceFeedClient.Parse(Header + row + row, Start, Start.AddHours(2)));
    }
    [Fact]
    public void XmlExternalEntitiesAndExcessiveDepthAreRejected()
    {
        Assert.Throws<XmlException>(() => ExportPriceFeedClient.Parse("<!DOCTYPE prices [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><prices>&secret;</prices>", Start, Start.AddHours(1)));
        Assert.Throws<InvalidDataException>(() => ExportPriceFeedClient.Parse("<prices><a><b><c><d><e/></d></c></b></a></prices>", Start, Start.AddHours(1)));
    }
    [Theory]
    [InlineData("http://example.com/a.csv")][InlineData("https://127.0.0.1/a.csv")][InlineData("https://[::1]/a.csv")]
    [InlineData("https://name:secret@example.com/a.csv")][InlineData("https://example.com:444/a.csv")][InlineData("https://host.local/a.csv")]
    public async Task UnsupportedAddressesFailBeforeNetwork(string url)
    {
        var handler = new Handler(_ => throw new Exception("Must not access the network"));
        await Assert.ThrowsAsync<ArgumentException>(() => new ExportPriceFeedClient(new HttpClient(handler)).ReadAsync(url, Start, Start.AddHours(1), default));
    }
    [Fact]
    public async Task BoundedTransportRejectsOversizedAndRedirectResponses()
    {
        var large = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', ExportPriceFeedClient.MaximumBytes + 1)) });
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExportPriceFeedClient(new HttpClient(large)).ReadAsync("https://example.com/prices.csv", Start, Start.AddHours(1), default));
        var redirect = new Handler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new("https://localhost/secret") } });
        await Assert.ThrowsAsync<HttpRequestException>(() => new ExportPriceFeedClient(new HttpClient(redirect)).ReadAsync("https://example.com/prices.csv", Start, Start.AddHours(1), default));
    }
    [Fact]
    public void ManualPriceHasExactKwhConversionAndDefaultsStayPse()
    {
        var options = new SolarSalesOptions(); options.Validate(); Assert.Equal("pse", options.PriceSource);
        var rows = ConfiguredExportPriceSource.Manual(0.123456m, Start, Start.AddHours(1));
        Assert.Equal(4, rows.Count); Assert.All(rows, row => Assert.Equal(123.456m, row.PricePlnPerMwh));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request)); }
}
