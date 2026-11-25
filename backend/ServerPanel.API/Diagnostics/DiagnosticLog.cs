using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;

namespace ServerPanel.API.Diagnostics;

public static class DiagnosticLog
{
    public const string ContextKey = "ArcadeNode.DiagnosticCode";

    public static T Mark<T>(T exception, DiagnosticPoint point) where T : Exception
    {
        if (Find(exception) is null) exception.Data[ContextKey] = point.Code;
        return exception;
    }

    public static DiagnosticPoint? Find(Exception exception)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            if (exception.Data[ContextKey] is string code && DiagnosticPoints.All.FirstOrDefault(p => p.Code == code) is { } point) return point;
            if (exception.InnerException is null) break;
            exception = exception.InnerException;
        }
        return null;
    }

    // Never pass the exception object, message, bodies, credentials or arbitrary URLs
    // to ILogger. Providers may serialize them. Only fixed messages and safe metadata.
    public static void Write(ILogger logger, DiagnosticPoint point, Exception? exception = null,
        Guid? serverId = null, Guid? nodeId = null, LogLevel? level = null,
        [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var root = exception;
        for (var depth = 0; depth < 7 && root?.InnerException is not null; depth++) root = root.InnerException;
        logger.Log(level ?? point.Level, new EventId(point.Number, point.Code),
            "[{DiagnosticCode}] {Portuguese} / {English}. Source={Source}:{Line} Member={Member} ServerId={ServerId} NodeId={NodeId} ErrorType={ErrorType} RootErrorType={RootErrorType} SqlNumber={SqlNumber} DockerStatus={DockerStatus} SocketError={SocketError}",
            point.Code, point.Portuguese, point.English, Path.GetFileName(file), line, member,
            serverId, nodeId, exception?.GetType().Name, root?.GetType().Name, (root as SqlException)?.Number,
            (root as Docker.DotNet.DockerApiException)?.StatusCode, (root as System.Net.Sockets.SocketException)?.SocketErrorCode);
    }

    public static async Task<T> RunAsync<T>(ILogger logger, DiagnosticPoint point, Func<Task<T>> action,
        Guid? serverId = null, Guid? nodeId = null,
        [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        logger.LogDebug(new EventId(point.Number, point.Code), "[{DiagnosticCode}] Iniciou etapa / Stage started. Source={Source}:{Line} Member={Member} ServerId={ServerId} NodeId={NodeId}",
            point.Code, Path.GetFileName(file), line, member, serverId, nodeId);
        try
        {
            var result = await action();
            if (result is bool success && !success) Write(logger, point, serverId: serverId, nodeId: nodeId, level: LogLevel.Warning, member: member, file: file, line: line);
            logger.LogDebug(new EventId(point.Number, point.Code), "[{DiagnosticCode}] Concluiu etapa / Stage completed. ServerId={ServerId} NodeId={NodeId}", point.Code, serverId, nodeId);
            return result;
        }
        catch (Exception exception)
        {
            Mark(exception, point);
            Write(logger, Find(exception) ?? point, exception, serverId, nodeId, member: member, file: file, line: line);
            throw;
        }
    }

    public static T Run<T>(ILogger logger, DiagnosticPoint point, Func<T> action,
        [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        try { return action(); }
        catch (Exception exception)
        {
            Mark(exception, point);
            Write(logger, Find(exception) ?? point, exception, member: member, file: file, line: line);
            throw;
        }
    }

    public static Task RunAsync(ILogger logger, DiagnosticPoint point, Func<Task> action,
        Guid? serverId = null, Guid? nodeId = null,
        [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        RunAsync(logger, point, async () => { await action(); return true; }, serverId, nodeId, member, file, line);

    public static DiagnosticPoint ForStatus(int status) => status switch
    {
        401 => DiagnosticPoints.Session, 403 => DiagnosticPoints.AccessDenied, 404 => DiagnosticPoints.NotFound,
        429 => DiagnosticPoints.RateLimited, 502 or 503 or 504 => DiagnosticPoints.Unavailable,
        >= 500 => DiagnosticPoints.Unexpected, _ => DiagnosticPoints.Validation
    };
}
