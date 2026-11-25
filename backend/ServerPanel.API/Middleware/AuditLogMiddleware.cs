using ServerPanel.API.Data;
using ServerPanel.API.Models;
using System.Diagnostics;
using System.Security.Claims;
using ServerPanel.API.Diagnostics;

namespace ServerPanel.API.Middleware;

/// <summary>
/// Records mutating HTTP requests (POST, PUT, PATCH, DELETE) to the AuditLog table
/// so administrators have a trail of who changed what and when. Read requests and
/// request/response bodies are intentionally not persisted (bodies may contain
/// credentials). Audit failures never interrupt the request.
/// </summary>
public class AuditLogMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AuditLogMiddleware> _logger;

    public AuditLogMiddleware(RequestDelegate next, ILogger<AuditLogMiddleware> logger, IServiceScopeFactory scopes)
    {
        _next = next;
        _logger = logger;
        _scopes = scopes;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsAuditableMethod(context.Request.Method))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            context.Items["AuditStatus"] = ApiExceptionHandler.Describe(ex).Status;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            await WriteAuditLogAsync(context, stopwatch.ElapsedMilliseconds);
        }
    }

    private static bool IsAuditableMethod(string method) =>
        HttpMethods.IsPost(method) ||
        HttpMethods.IsPut(method) ||
        HttpMethods.IsPatch(method) ||
        HttpMethods.IsDelete(method);

    private async Task WriteAuditLogAsync(HttpContext context, long durationMs)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();

            Guid? userId = null;
            if (Guid.TryParse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var parsed))
            {
                userId = parsed;
            }

            userId ??= context.Items["AuditUserId"] as Guid?;
            db.AuditLogs.Add(new AuditLog
            {
                Timestamp = DateTime.UtcNow,
                UserId = userId,
                Username = context.User.FindFirst(ClaimTypes.Name)?.Value ?? context.Items["AuditUsername"] as string,
                Method = context.Request.Method,
                Path = Truncate(context.Request.Path.Value ?? string.Empty, 2048),
                StatusCode = context.Items["AuditStatus"] as int? ?? context.Response.StatusCode,
                IpAddress = context.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Truncate(context.Request.Headers.UserAgent.ToString(), 512),
                DurationMs = durationMs
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(_logger, DiagnosticPoints.AuditWrite, ex);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>
/// Extension method to register the audit logging middleware in the pipeline.
/// </summary>
public static class AuditLogMiddlewareExtensions
{
    public static IApplicationBuilder UseAuditLogging(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<AuditLogMiddleware>();
    }
}
