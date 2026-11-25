using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Net;
using ArcadeNode.Transfers;
using ServerPanel.API.Common;
using ServerPanel.API.DTOs;
using ServerPanel.API.Models;
using ServerPanel.API.Services;
using ServerPanel.API.Security;

namespace ServerPanel.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ServersController : ControllerBase
{
    private readonly IServerService _serverService;
    private readonly IDockerService _dockerService;
    private readonly ILogger<ServersController> _logger;
    private readonly ServerAccess _access;

    public ServersController(IServerService serverService, IDockerService dockerService, ILogger<ServersController> logger, ServerAccess access)
    {
        _serverService = serverService;
        _dockerService = dockerService;
        _logger = logger;
        _access = access;
    }

    /// <summary>
    /// Get all servers (admin) or user's servers
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ServerDto>), 200)]
    public async Task<IActionResult> GetServers([FromQuery] int? page = null, [FromQuery] int? pageSize = null)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var (p, ps) = Pagination.Resolve(page, pageSize);

        var isAdmin = User.IsInRole("Admin") || User.IsInRole("SuperAdmin");
        var result = isAdmin
            ? await _serverService.GetAllServersAsync(p, ps)
            : await _serverService.GetUserServersAsync(userId.Value, p, ps);

        Response.ApplyPaginationHeaders(result);
        return Ok(result.Items);
    }

    /// <summary>
    /// Get server by ID
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ServerDto), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetServer(Guid id)
    {
        var server = await _serverService.GetServerByIdAsync(id);
        if (server == null) return NotFound();

        return await DescribeServerAsync(server);
    }

    /// <summary>
    /// Get server by identifier (short ID)
    /// </summary>
    [HttpGet("identifier/{identifier:length(8)}")]
    [ProducesResponseType(typeof(ServerDto), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetServerByIdentifier(string identifier)
    {
        var server = await _serverService.GetServerByIdentifierAsync(identifier);
        if (server == null) return NotFound();

        return await DescribeServerAsync(server);
    }

    /// <summary>
    /// Create a new server
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin,SuperAdmin")]
    [ProducesResponseType(typeof(ServerDto), 201)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> CreateServer([FromBody] CreateServerRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var server = await _serverService.CreateServerAsync(request, userId.Value);
        if (server == null)
        {
            return BadRequest(new { message = "Failed to create server" });
        }

        return CreatedAtAction(nameof(GetServer), new { id = server.Id }, server);
    }

    /// <summary>
    /// Create a new server from a cartridge definition
    /// </summary>
    [HttpPost("from-egg/{eggId:guid}")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> CreateServerFromEgg(Guid eggId, [FromBody] CreateEggServerRequest request)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        var server = await _serverService.CreateServerFromEggAsync(eggId, request, userId.Value);
        return server is null ? NotFound() : CreatedAtAction(nameof(GetServer), new { id = server.Id }, server);
    }

    [HttpPost("from-cartridge")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    [ProducesResponseType(typeof(ServerDto), 201)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> CreateServerFromCartridge([FromBody] CreateCartridgeServerRequest request)
    {
        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
            return BadRequest(new { message = "Validation failed", errors = errors });
        }
        
        if (request is null)
        {
            return BadRequest(new { message = "Request body is required" });
        }

        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var server = await _serverService.CreateServerFromCartridgeAsync(request, userId.Value);
        if (server == null)
        {
            return BadRequest(new { message = "Failed to create server. Check cartridge ID, node ID, and variable values." });
        }

        return CreatedAtAction(nameof(GetServer), new { id = server.Id }, server);
    }

    /// <summary>
    /// Update server settings
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ServerDto), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateServer(Guid id, [FromBody] UpdateServerRequest request)
    {
        var server = await _serverService.GetServerByIdAsync(id);
        if (server == null) return NotFound();

        if (!await CanAccessServerAsync(server, ServerPermission.Rename)) return Forbid();

        var updated = await _serverService.UpdateServerAsync(id, request);
        return updated is null ? NotFound() : await DescribeServerAsync(updated);
    }

    /// <summary>
    /// Delete a server
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> DeleteServer(Guid id)
    {
        var success = await _serverService.DeleteServerAsync(id);
        if (!success) return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Send power action to server (start, stop, restart, kill)
    /// </summary>
    [HttpPost("{id:guid}/power")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> PowerAction(Guid id, [FromBody] PowerActionRequest request)
    {
        var server = await _serverService.GetServerByIdAsync(id);
        if (server == null) return NotFound();

        var permission = ServerAccess.Power(request.Action);
        if (!await CanAccessServerAsync(server, permission ?? ServerPermission.View)) return Forbid();
        if (permission is null) return BadRequest(new { message = "Invalid power action" });

        if (server.IsSuspended)
        {
            return BadRequest(new { message = "Server is suspended" });
        }

        if (string.IsNullOrEmpty(server.ContainerId))
        {
            return BadRequest(new { message = "Server container not found" });
        }

        bool success;
        ServerStatus newStatus;
        var action = request.Action.ToLowerInvariant();

        switch (action)
        {
            case "start":
                await _serverService.UpdateServerStatusAsync(id, ServerStatus.Starting);
                success = await _dockerService.StartContainerAsync(id);
                newStatus = success ? ServerStatus.Running : ServerStatus.Error;
                break;

            case "stop":
                await _serverService.UpdateServerStatusAsync(id, ServerStatus.Stopping);
                success = await _dockerService.StopContainerAsync(id);
                newStatus = success ? ServerStatus.Stopped : ServerStatus.Error;
                break;

            case "restart":
                await _serverService.UpdateServerStatusAsync(id, ServerStatus.Stopping);
                success = await _dockerService.RestartContainerAsync(id);
                newStatus = success ? ServerStatus.Running : ServerStatus.Error;
                break;

            case "kill":
                success = await _dockerService.KillContainerAsync(id);
                newStatus = ServerStatus.Stopped;
                break;

            default:
                return BadRequest(new { message = "Invalid power action" });
        }

        if (!success && action is "stop" or "restart")
        {
            try
            {
                var observed = await _dockerService.ObserveRuntimeAsync(id, HttpContext.RequestAborted);
                if (observed is not null) newStatus = observed.Status;
            }
            catch (Exception error) when (error is IOException or HttpRequestException or Docker.DotNet.DockerApiException or OperationCanceledException)
            {
                _logger.LogWarning("Could not confirm the runtime after a server power action ({ErrorType}).",
                    error.GetType().Name);
            }
        }

        await _serverService.UpdateServerStatusAsync(id, newStatus);

        return Ok(new { message = success ? $"Server {action} command sent" : $"Server {action} could not be confirmed", success });
    }

    /// <summary>
    /// Get server console logs
    /// </summary>
    [HttpGet("{id:guid}/logs")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetLogs(Guid id, [FromQuery, System.ComponentModel.DataAnnotations.Range(1,1000)] int tail = 100)
    {
        var server = await _serverService.GetServerByIdAsync(id);
        if (server == null) return NotFound();

        if (!await CanAccessServerAsync(server, ServerPermission.ConsoleRead)) return Forbid();

        if (string.IsNullOrEmpty(server.ContainerId))
        {
            return Ok(new { logs = "" });
        }

        var logs = await _dockerService.GetContainerLogsAsync(id, tail);
        return Ok(new { logs });
    }

    [HttpGet("{id:guid}/logs/live")]
    public async Task<IActionResult> FollowLogs(Guid id,
        [FromQuery, System.ComponentModel.DataAnnotations.Range(1,200)] int tail = 100)
    {
        var server = await _serverService.GetServerByIdAsync(id);
        if (server is null) return NotFound();
        if (!await CanAccessServerAsync(server, ServerPermission.ConsoleRead)) return Forbid();
        if (server.IsSuspended || string.IsNullOrEmpty(server.ContainerId)) return Conflict();

        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.ContentType = "text/plain; charset=utf-8";
        using var window = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        window.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await _dockerService.FollowContainerLogsAsync(id, tail, async (content, token) =>
            {
                await Response.Body.WriteAsync(content, token);
                await Response.Body.FlushAsync(token);
            }, window.Token);
        }
        catch (OperationCanceledException) when (window.IsCancellationRequested)
        {
            // The next connection reauthorizes the session and refreshes the bounded tail.
        }
        catch (Exception error) when (Response.HasStarted && error is IOException or HttpRequestException or Docker.DotNet.DockerApiException)
        {
            _logger.LogWarning("Live console stream closed for server {ServerId} ({ErrorType}).", id, error.GetType().Name);
        }
        return new EmptyResult();
    }

    [HttpGet("{id:guid}/files")]
    public async Task<IActionResult> ListFiles(Guid id, [FromQuery] string? path, [FromQuery] string? cursor, [FromQuery] string? filter,
        [FromServices] NodeFileClient files, CancellationToken cancellationToken)
    {
        var server = await _serverService.GetServerByIdAsync(id);
        if (server is null) return NotFound();
        if (!await CanAccessServerAsync(server, ServerPermission.FileList)) return Forbid();
        if (server.IsSuspended) return StatusCode(StatusCodes.Status409Conflict);
        if (!string.IsNullOrEmpty(path) && !SecurityLimits.ValidGamePath(path)) return BadRequest();
        if (!GameDirectoryQuery.ValidCursor(cursor) || !GameDirectoryQuery.ValidFilter(filter)) return BadRequest();

        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        var (status, bytes) = await files.ListAsync(server.NodeId, server.Id, path ?? "", cursor, filter, cancellationToken);
        return bytes is null ? StatusCode((int)status) : File(bytes, "application/json; charset=utf-8");
    }

    [HttpGet("{id:guid}/storage")]
    public async Task<IActionResult> GetStorage(Guid id, [FromServices] NodeFileClient files, CancellationToken token)
    {
        var server = await _serverService.GetServerByIdAsync(id);
        if (server is null) return NotFound();
        if (!await CanAccessServerAsync(server, ServerPermission.View)) return Forbid();
        if (server.IsSuspended) return StatusCode(StatusCodes.Status409Conflict);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        var (status, bytes) = await files.StorageAsync(server.NodeId, server.Id, token);
        return bytes is null ? StatusCode((int)status) : File(bytes, "application/json; charset=utf-8");
    }

    [HttpGet("{id:guid}/files/download")]
    public async Task<IActionResult> DownloadFile(Guid id, [FromQuery] string path, [FromServices] NodeFileClient files, CancellationToken cancellationToken)
    {
        var server = await _serverService.GetServerByIdAsync(id);
        if (server is null) return NotFound();
        if (!await CanAccessServerAsync(server, ServerPermission.FileRead)) return Forbid();
        if (server.IsSuspended) return StatusCode(StatusCodes.Status409Conflict);
        if (!SecurityLimits.ValidGamePath(path)) return BadRequest();

        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        var (status, bytes) = await files.DownloadAsync(server.NodeId, server.Id, path, cancellationToken);
        return bytes is null ? StatusCode((int)status) : File(bytes, "application/octet-stream", Path.GetFileName(path));
    }


    [HttpPost("{id:guid}/files/directories")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> CreateDirectory(Guid id, DirectoryInput input,
        [FromServices] NodeFileClient files, [FromServices] IServiceScopeFactory scopes, CancellationToken token)
    {
        if (!SecurityLimits.ValidGamePath(input.Path)) return BadRequest();
        return await ChangeStoppedFilesAsync(id, files, scopes, ServerPermission.FileCreate,
            (nodeId, request) => files.CreateDirectoryAsync(nodeId, id, input.Path, request), token);
    }

    [HttpPost("{id:guid}/files/move")]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> MoveEntry(Guid id, FileTransferInput input,
        [FromServices] NodeFileClient files, [FromServices] IServiceScopeFactory scopes, CancellationToken token)
    {
        if (!SecurityLimits.ValidGamePath(input.Source) || !SecurityLimits.ValidGamePath(input.Destination) || input.Source == input.Destination ||
            input.Destination.StartsWith(input.Source + '/', StringComparison.Ordinal)) return BadRequest();
        return await ChangeStoppedFilesAsync(id, files, scopes, ServerPermission.FileUpdate,
            (nodeId, request) => files.MoveAsync(nodeId, id, input.Source, input.Destination, request), token);
    }

    [HttpPost("{id:guid}/files/chmod")]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> ChangeFilePermissions(Guid id, FileModeInput input,
        [FromServices] NodeFileClient files, [FromServices] IServiceScopeFactory scopes, CancellationToken token)
    {
        var directory = input.Directory ?? "";
        if ((directory.Length != 0 && !SecurityLimits.ValidGamePath(directory)) || input.Entries.Any(name => !IsFileEntryName(name)) ||
            input.Entries.Distinct(StringComparer.Ordinal).Count() != input.Entries.Length) return BadRequest();
        return await ChangeStoppedFilesAsync(id, files, scopes, ServerPermission.FileUpdate,
            (nodeId, request) => files.ChangePermissionsAsync(nodeId, id, directory, input.Entries, input.Mode, request), token);
    }

    [HttpPost("{id:guid}/files/copy")]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> CopyFile(Guid id, FileTransferInput input,
        [FromServices] NodeFileClient files, [FromServices] IServiceScopeFactory scopes, CancellationToken token)
    {
        if (!SecurityLimits.ValidGamePath(input.Source) || !SecurityLimits.ValidGamePath(input.Destination) || input.Source == input.Destination) return BadRequest();
        return await ChangeStoppedFilesAsync(id, files, scopes, ServerPermission.FileCreate,
            (nodeId, request) => files.CopyAsync(nodeId, id, input.Source, input.Destination, request), token);
    }

    [HttpPost("{id:guid}/files/compress")]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> CompressFiles(Guid id, FileArchiveInput input,
        [FromServices] NodeFileClient files, [FromServices] IServiceScopeFactory scopes, CancellationToken token)
    {
        var directory = input.Directory ?? "";
        if ((directory.Length != 0 && !SecurityLimits.ValidGamePath(directory)) || !IsFileEntryName(input.Destination) ||
            !input.Destination.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            input.Entries.Any(name => !IsFileEntryName(name) || name == input.Destination) ||
            input.Entries.Distinct(StringComparer.Ordinal).Count() != input.Entries.Length) return BadRequest();
        return await ChangeStoppedFilesAsync(id, files, scopes, ServerPermission.FileArchive,
            (nodeId, request) => files.CompressAsync(nodeId, id, directory, input.Entries, input.Destination, request), token);
    }

    [HttpPost("{id:guid}/files/extract")]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> ExtractArchive(Guid id, FileTransferInput input,
        [FromServices] NodeFileClient files, [FromServices] IServiceScopeFactory scopes, CancellationToken token)
    {
        if (!SecurityLimits.ValidGamePath(input.Source) || !SecurityLimits.ValidGamePath(input.Destination) || input.Source == input.Destination ||
            !(input.Source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || input.Source.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ||
              input.Source.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || input.Source.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)))
            return BadRequest();
        return await ChangeStoppedFilesAsync(id, files, scopes, ServerPermission.FileCreate,
            (nodeId, request) => files.ExtractAsync(nodeId, id, input.Source, input.Destination, request), token);
    }

    [HttpPost("{id:guid}/files/delete")]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> DeleteFileEntries(Guid id, FileDeleteInput input,
        [FromServices] NodeFileClient files, [FromServices] IServiceScopeFactory scopes, CancellationToken token)
    {
        var directory = input.Directory ?? "";
        if ((directory.Length != 0 && !SecurityLimits.ValidGamePath(directory)) || input.Entries.Any(name => !IsFileEntryName(name)) ||
            input.Entries.Distinct(StringComparer.Ordinal).Count() != input.Entries.Length) return BadRequest();
        return await ChangeStoppedFilesAsync(id, files, scopes, ServerPermission.FileDelete,
            (nodeId, request) => files.DeleteEntriesAsync(nodeId, id, directory, input.Entries, request), token);
    }

    private static bool IsFileEntryName(string? name) => name is not null && SecurityLimits.ValidGamePath(name) && !name.Contains('/') &&
        System.Text.Encoding.UTF8.GetByteCount(name) <= 255;

    private async Task<IActionResult> ChangeStoppedFilesAsync(Guid id, NodeFileClient files, IServiceScopeFactory scopes, ServerPermission permission,
        Func<Guid, CancellationToken, Task<HttpStatusCode>> operation, CancellationToken token)
    {
        var server = await _serverService.GetServerByIdAsync(id);
        if (server is null) return NotFound();
        if (!await CanAccessServerAsync(server, permission)) return Forbid();

        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerPanel.API.Security.ServerOperationGate>();
        await using var lease = await gate.AcquireAsync(id, token);
        server = await _serverService.GetServerByIdAsync(id);
        if (server is null) return NotFound();
        if (!await CanAccessServerAsync(server, permission)) return Forbid();
        if (server.IsSuspended || server.Status != ServerStatus.Stopped || string.IsNullOrEmpty(server.ContainerId))
            return Conflict(new { message = "Stop the server before changing file paths." });
        if (!files.IsConfigured(server.NodeId)) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (!await _dockerService.IsContainerStoppedAsync(id, token))
            return Conflict(new { message = "Stop the server before changing file paths." });

        Response.Headers.CacheControl = "no-store";
        return StatusCode((int)await operation(server.NodeId, token));
    }

    public sealed record DirectoryInput([Required, StringLength(1024)] string Path);
    public sealed record FileTransferInput([Required, StringLength(1024)] string Source, [Required, StringLength(1024)] string Destination);
    public sealed record FileArchiveInput([StringLength(1024)] string? Directory, [Required, MinLength(1), MaxLength(40)] string[] Entries,
        [Required, StringLength(255)] string Destination);
    public sealed record FileDeleteInput([StringLength(1024)] string? Directory, [Required, MinLength(1), MaxLength(40)] string[] Entries);
    public sealed record FileModeInput([StringLength(1024)] string? Directory, [Required, MinLength(1), MaxLength(40)] string[] Entries,
        [Required, RegularExpression("\\A0?[0-7]{3}\\z")] string Mode);

    [HttpPut("{id:guid}/files/text")]
    public async Task<IActionResult> EditTextFile(Guid id, [FromQuery] string path, [FromBody] TextEdit edit,
        [FromServices] NodeFileClient files, [FromServices] IServiceScopeFactory scopes, CancellationToken token)
    {
        var server = await _serverService.GetServerByIdAsync(id);
        if (server is null) return NotFound();
        if (!await CanAccessServerAsync(server, ServerPermission.FileCreate)) return Forbid();
        if (!SecurityLimits.ValidGamePath(path) || edit.Content is null || edit.Content.Contains('\0') ||
            System.Text.Encoding.UTF8.GetByteCount(edit.Content) > 8192 ||
            edit.Sha256 is not { Length: 64 } || !edit.Sha256.All(char.IsAsciiHexDigit)) return BadRequest();

        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerPanel.API.Security.ServerOperationGate>();
        await using var lease = await gate.AcquireAsync(id, token);
        server = await _serverService.GetServerByIdAsync(id);
        if (server is null) return NotFound();
        if (!await CanAccessServerAsync(server, ServerPermission.FileCreate)) return Forbid();
        if (server.IsSuspended || server.Status != ServerStatus.Stopped || string.IsNullOrEmpty(server.ContainerId) ||
            !await _dockerService.IsContainerStoppedAsync(id, token))
            return Conflict(new { message = "Stop the server before editing files." });

        Response.Headers.CacheControl = "no-store";
        return StatusCode((int)await files.EditTextAsync(server.NodeId, id, path, edit.Content, edit.Sha256, token));
    }

    public sealed record TextEdit(string? Content, string? Sha256);

    [HttpPost("{id:guid}/assistant")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("assistant")]
    public async Task<IActionResult> AskAssistant(Guid id, [FromBody] AssistantQuestion question,
        [FromServices] LocalGameAssistant assistant, CancellationToken token)
    {
        var server = await _serverService.GetServerByIdAsync(id);
        if (server is null) return NotFound();
        var permission = question.IncludeRuntime ? ServerPermission.Assistant | ServerPermission.View : ServerPermission.Assistant;
        if (!await CanAccessServerAsync(server, permission)) return Forbid();
        if (string.IsNullOrWhiteSpace(question.Question)) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        Func<CancellationToken, Task<ServerStatsDto?>>? observe = question.IncludeRuntime &&
            !server.IsSuspended && !string.IsNullOrEmpty(server.ContainerId)
            ? cancellation => _dockerService.GetContainerStatsAsync(id, cancellation)
            : null;
        var result = await assistant.AskAsync(server, question.Question.Trim(), observe, token);
        return result.Answer is not null ? Ok(new { answer = result.Answer, runtimeIncluded = result.RuntimeIncluded }) : StatusCode(result.Status);
    }

    public sealed record AssistantQuestion(
        [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(2000)] string Question,
        bool IncludeRuntime = false);

    [HttpPost("{id:guid}/files/upload")]
    [RequestSizeLimit(GameFileTransfer.MaximumBytes)]
    [Consumes("application/octet-stream")]
    public async Task<IActionResult> UploadFile(Guid id, [FromQuery] string path, [FromServices] NodeFileClient files,
        [FromServices] IServiceScopeFactory scopes, CancellationToken cancellationToken)
    {
        var server = await _serverService.GetServerByIdAsync(id);
        if (server is null) return NotFound();
        if (!await CanAccessServerAsync(server, ServerPermission.FileCreate)) return Forbid();
        if (server.IsSuspended) return StatusCode(StatusCodes.Status409Conflict);
        if (!SecurityLimits.ValidGamePath(path)) return BadRequest();
        if (Request.ContentLength is not { } length) return StatusCode(StatusCodes.Status411LengthRequired);
        if (length > GameFileTransfer.MaximumBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (!files.IsConfigured(server.NodeId)) return StatusCode(StatusCodes.Status503ServiceUnavailable);

        Response.Headers.CacheControl = "no-store";
        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerPanel.API.Security.ServerOperationGate>();
        await using var lease = await gate.AcquireAsync(id, cancellationToken);
        server = await _serverService.GetServerByIdAsync(id);
        if (server is null) return NotFound();
        if (!await CanAccessServerAsync(server, ServerPermission.FileCreate)) return Forbid();
        if (server.IsSuspended) return StatusCode(StatusCodes.Status409Conflict);
        if (server.DiskLimit > 0 && length > (long)server.DiskLimit * 1024 * 1024)
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        var status = await files.UploadAsync(server.NodeId, server.Id, path, Request.Body, length, cancellationToken);
        return StatusCode((int)status);
    }

    /// <summary>
    /// Send command to server console
    /// </summary>
    [HttpPost("{id:guid}/command")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> SendCommand(Guid id, [FromBody] ConsoleCommandRequest request)
    {
        var server = await _serverService.GetServerByIdAsync(id);
        if (server == null) return NotFound();

        if (!await CanAccessServerAsync(server, ServerPermission.ConsoleCommand)) return Forbid();

        if (server.IsSuspended || server.Status != ServerStatus.Running)
        {
            return BadRequest(new { message = "Server is not running" });
        }

        if (string.IsNullOrEmpty(server.ContainerId))
        {
            return BadRequest(new { message = "Server container not found" });
        }

        var success = await _dockerService.SendCommandAsync(id, request.Command);
        return Ok(new { success });
    }

    /// <summary>
    /// Get server resource usage stats
    /// </summary>
    [HttpGet("{id:guid}/stats")]
    [ProducesResponseType(typeof(ServerStatsDto), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetStats(Guid id, CancellationToken token)
    {
        var server = await _serverService.GetServerByIdAsync(id);
        if (server == null) return NotFound();

        if (!await CanAccessServerAsync(server, ServerPermission.View)) return Forbid();

        if (server.IsSuspended || string.IsNullOrEmpty(server.ContainerId))
        {
            return Ok(new ServerStatsDto(id, server.Status, 0, 0, null, 0, 0, 0, DateTime.UtcNow));
        }

        var stats = await _dockerService.GetContainerStatsAsync(id, token);
        return Ok(stats ?? new ServerStatsDto(id, server.Status, 0, 0, null, 0, 0, 0, DateTime.UtcNow));
    }

    /// <summary>
    /// Suspend or unsuspend a server
    /// </summary>
    [HttpPost("{id:guid}/suspend")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    [ProducesResponseType(200)]
    public async Task<IActionResult> SuspendServer(Guid id, [FromQuery] bool suspended = true)
    {
        var success = await _serverService.SuspendServerAsync(id, suspended);
        if (!success) return NotFound();

        return Ok(new { message = suspended ? "Server suspended" : "Server unsuspended" });
    }

    private Guid? GetUserId() => ServerAccess.UserId(User);

    private Task<bool> CanAccessServerAsync(ServerDto server, ServerPermission permission) =>
        _access.AllowsAsync(User, server.Id, server.OwnerId, permission, HttpContext.RequestAborted);

    private async Task<IActionResult> DescribeServerAsync(ServerDto server)
    {
        if (ServerAccess.CanManage(User, server.OwnerId)) return Ok(server);
        var permissions = await _access.ResolveAsync(User, server.Id, server.OwnerId, HttpContext.RequestAborted);
        return permissions is { } allowed
            ? Ok(server with { StartupCommand = null, SharedPermissions = ServerAccess.Describe(allowed) }) : Forbid();
    }
}
