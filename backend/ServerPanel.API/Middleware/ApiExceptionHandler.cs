using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Diagnostics;

namespace ServerPanel.API.Middleware;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public static (int Status, string Title) Describe(Exception exception) => exception switch
        {
            ValidationException => (StatusCodes.Status400BadRequest, exception.Message),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "The resource changed. Refresh and try again."),
            DbUpdateException => (StatusCodes.Status409Conflict, "The resource could not be saved. Refresh and try again."),
            InvalidOperationException => (StatusCodes.Status409Conflict, "Operation unavailable. Check resource configuration and capacity."),
            OperationCanceledException => (StatusCodes.Status504GatewayTimeout, "The operation timed out."),
            Docker.DotNet.DockerApiException or HttpRequestException or IOException or System.Security.Authentication.AuthenticationException
                => (StatusCodes.Status502BadGateway, "The node did not complete the operation."),
            _ => (StatusCodes.Status500InternalServerError, "The operation could not be completed.")
        };

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = Describe(exception);
        var point = DiagnosticLog.Find(exception) ?? (exception is DbUpdateException ? DiagnosticPoints.DatabaseWrite :
            exception is Microsoft.Data.SqlClient.SqlException ? DiagnosticPoints.DatabaseConnection : DiagnosticLog.ForStatus(status));
        context.Items[DiagnosticLog.ContextKey] = point.Code;
        DiagnosticLog.Write(logger, point, exception, level: status >= 500 ? LogLevel.Error : LogLevel.Warning);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title,
            Extensions = { ["traceId"] = context.TraceIdentifier, ["diagnosticCode"] = point.Code } }, cancellationToken);
        return true;
    }
}
