using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using ServerPanel.API.Diagnostics;

namespace ServerPanel.API.Middleware;

public sealed class RequestDiagnosticMiddleware(RequestDelegate next, ILogger<RequestDiagnosticMiddleware> logger, IConfiguration configuration)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var scope = logger.BeginScope("TraceId={TraceId}", context.TraceIdentifier);
        var clock = Stopwatch.StartNew();
        context.Response.OnStarting(() =>
        {
            // Exception handling can clear headers before writing ProblemDetails.
            context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
            if (context.Response.StatusCode >= 400)
                context.Response.Headers["X-Diagnostic-Code"] = Resolve(context).Code;
            return Task.CompletedTask;
        });
        try { await next(context); }
        catch (Exception exception)
        {
            var point = context.RequestAborted.IsCancellationRequested ? DiagnosticPoints.RequestAborted : DiagnosticLog.Find(exception) ?? DiagnosticPoints.Unexpected;
            context.Items[DiagnosticLog.ContextKey] = point.Code;
            DiagnosticLog.Write(logger, point, exception);
            throw;
        }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            var status = context.Response.StatusCode;
            if (status >= 400)
            {
                var point = Resolve(context);
                logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning, new EventId(point.Number, point.Code),
                    "[{DiagnosticCode}] Requisição falhou / Request failed. Status={Status} Route={Route} ElapsedMs={ElapsedMs}", point.Code, status, route, clock.ElapsedMilliseconds);
            }
            else if (clock.ElapsedMilliseconds >= Math.Clamp(configuration.GetValue("Diagnostics:SlowRequestMilliseconds", 3000), 100, 120000))
            {
                var point = DiagnosticPoints.SlowRequest;
                logger.LogWarning(new EventId(point.Number, point.Code), "[{DiagnosticCode}] {Portuguese} / {English}. Route={Route} ElapsedMs={ElapsedMs}",
                    point.Code, point.Portuguese, point.English, route, clock.ElapsedMilliseconds);
            }
        }
    }

    private static DiagnosticPoint Resolve(HttpContext context) =>
        DiagnosticPoints.All.FirstOrDefault(point => point.Code == context.Items[DiagnosticLog.ContextKey] as string)
        ?? DiagnosticLog.ForStatus(context.Response.StatusCode);
}
