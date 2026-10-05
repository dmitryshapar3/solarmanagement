using Microsoft.AspNetCore.Builder;

namespace DeyeSolar.Web.Tests;

/// <summary>Shutdown failures must not leave either the host or its isolated SQL database behind.</summary>
internal static class TestHttpHostCleanup
{
    public static async ValueTask DisposeAsync(WebApplication? app, SqlServerTestDatabase database, bool stop = false)
    {
        try { await DisposeApplicationAsync(app, stop); }
        finally { await database.DisposeAsync(); }
    }

    // Some fixtures own provider clients and signing keys between application and database disposal.
    public static async ValueTask DisposeApplicationAsync(WebApplication? app, bool stop = false)
    {
        if (app is null) return;
        try { if (stop) await app.StopAsync(); }
        finally { await app.DisposeAsync(); }
    }
}
