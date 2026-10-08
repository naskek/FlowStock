using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

// Only exclusive /api/tsd/** routes. Shared WPF/PC/TSD routes remain a separate gate.
public static class TsdSessionAuthorization
{
    public static async Task InvokeAsync(HttpContext context, Func<Task> next, TsdSessionStore sessions)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api/tsd", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/tsd/login", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }
        using var lease = sessions.OpenLease(context.Request, DateTimeOffset.UtcNow);
        if (lease == null)
        {
            context.Response.Cookies.Delete(TsdSessionStore.CookieName,
                new CookieOptions { Path = "/api", Secure = true });
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { ok = false, error = "INVALID_TSD_SESSION" });
            return;
        }
        if (await HasForeignDeviceIdAsync(context.Request, lease.DeviceId))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { ok = false, error = "TSD_DEVICE_ID_MISMATCH" });
            return;
        }
        context.Items[TsdSessionStore.VerifiedDeviceIdItem] = lease.DeviceId;
        await next();
    }

    private static async Task<bool> HasForeignDeviceIdAsync(HttpRequest request, string deviceId)
    {
        foreach (var pair in request.Query)
        {
            if ((string.Equals(pair.Key, "device_id", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, "deviceId", StringComparison.OrdinalIgnoreCase))
                && (pair.Value.Count != 1
                    || !string.Equals(pair.Value[0], deviceId, StringComparison.Ordinal)))
                return true;
        }
        if (!(HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPatch(request.Method) || HttpMethods.IsDelete(request.Method))
            || request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            return false;

        request.EnableBuffering();
        try
        {
            using var json = await JsonDocument.ParseAsync(request.Body,
                cancellationToken: request.HttpContext.RequestAborted);
            return HasMismatch(json.RootElement, deviceId);
        }
        catch (JsonException)
        {
            return false; // existing handler reports malformed JSON
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    private static bool HasMismatch(JsonElement element, string identity)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(child => HasMismatch(child, identity));
        if (element.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, "device_id", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "deviceId", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.String
                    || !string.Equals(property.Value.GetString(), identity, StringComparison.Ordinal))
                    return true;
            }
            else if (HasMismatch(property.Value, identity)) return true;
        }
        return false;
    }
}
