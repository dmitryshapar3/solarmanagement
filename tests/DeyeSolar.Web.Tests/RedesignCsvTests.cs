using System.Globalization;
using DeyeSolar.Web.Redesign;
using Microsoft.AspNetCore.Http;

namespace DeyeSolar.Web.Tests;

public sealed class RedesignCsvTests
{
    [Fact]
    public async Task CsvKeepsNullBlankSignedNumbersInvariantAndEscapesUserSuppliedFormulaCells()
    {
        var previous=CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("pl-PL");
        try
        {
            var context=new DefaultHttpContext();context.Response.Body=new MemoryStream();
            var csv=new CsvDownload("readings.csv",async (writer,_)=>
                await CsvDownload.Row(writer,null,0m,-0.125m,true,new DateTimeOffset(2026,10,6,12,0,0,TimeSpan.Zero),"=SUM(A1)","+name","-name","@name","a,\"b\"\nnext"));
            await csv.ExecuteAsync(context);
            Assert.Equal("text/csv; charset=utf-8",context.Response.ContentType);
            Assert.Equal("no-store",context.Response.Headers.CacheControl);
            Assert.Equal("attachment; filename=\"readings.csv\"",context.Response.Headers.ContentDisposition);
            context.Response.Body.Position=0;
            var actual=await new StreamReader(context.Response.Body).ReadToEndAsync();
            Assert.StartsWith("\"\",\"0\",\"-0.125\",\"true\",\"2026-10-06T12:00:00.0000000+00:00\"",actual);
            Assert.Contains("\"'=SUM(A1)\",\"'+name\",\"'-name\",\"'@name\"",actual);
            Assert.Contains("\"a,\"\"b\"\"\nnext\"",actual);
        }
        finally {CultureInfo.CurrentCulture=previous;}
    }
}
