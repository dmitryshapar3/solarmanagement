using System.Globalization;
using DeyeSolar.Web.Services;

namespace DeyeSolar.Web.Api;

public static class ExportSalesApi
{
    public static void MapExportSalesApi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/sales", async (HttpContext context, IExportSalesService service, CancellationToken ct) =>
        {
            var query = context.Request.Query;
            if (!Enum.TryParse<ExportSalesPeriod>(query["period"].ToString(), true, out var period) || !Enum.IsDefined(period)
                || !DateOnly.TryParseExact(query["date"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return Results.BadRequest(new { error = "Choose a valid sales period and date." });
            DateOnly? Parse(string key) => DateOnly.TryParseExact(query[key], "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
            try
            {
                var request = new ExportSalesRequest(period, date, Parse("from"), Parse("through"));
                return Results.Ok(query["details"] == "true" ? await service.ReadDetailsAsync(request, ct) : await service.ReadAsync(request, ct));
            }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Choose a valid sales date range." }); }
        }).RequireAuthorization(ApiAuthorization.AuthenticatedUser);
    }
}
