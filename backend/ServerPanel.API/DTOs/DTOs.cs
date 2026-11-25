using ServerPanel.API.Models;
using ServerPanel.API.Validation;
using System.ComponentModel.DataAnnotations;

namespace ServerPanel.API.DTOs;

// Auth DTOs
public record LoginRequest(
    [Required][MaxLength(100)] string Username,
    [Required][MaxLength(72)] string Password,
    [MaxLength(6)] string? TotpCode = null
);

public record RegisterRequest(
    [Required][MaxLength(100)] string Username,
    [Required][EmailAddress][MaxLength(255)] string Email,
    [Required][PasswordComplexity] string Password,
    [Required][MaxLength(32)] string InviteCode,
    [MaxLength(50)] string? FirstName,
    [MaxLength(50)] string? LastName
);

public record AuthResponse(
    string Token,
    UserDto User,
    DateTime ExpiresAt
);

public record UserDto(
    Guid Id,
    string Username,
    string Email,
    string FirstName,
    string LastName,
    UserRole Role,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? LastLoginAt
);

// Server DTOs
public record CreateServerRequest(
    [Required][MaxLength(100)] string Name,
    [MaxLength(500)] string? Description,
    [Required] Guid NodeId,
    Guid? EggId,
    [Required] string DockerImage,
    string? StartupCommand,
    [Range(128, 1048576)] int MemoryLimit = 1024,
    [Range(1, 6400)] int CpuLimit = 100,
    [Range(256, 10485760)] int DiskLimit = 10240,
    [Range(1024, 65535)] int Port = 25565,
    Dictionary<string, string>? EnvironmentVariables = null,
    int[]? CpuCores = null
);

public record UpdateServerRequest(
    [MaxLength(100)] string? Name,
    [MaxLength(500)] string? Description,
    string? StartupCommand,
    [Range(128, 1048576)] int? MemoryLimit,
    [Range(1, 6400)] int? CpuLimit,
    [Range(256, 10485760)] int? DiskLimit,
    Dictionary<string, string>? EnvironmentVariables
);

public record ServerDto(
    Guid Id,
    string Name,
    string Description,
    string Identifier,
    string? ContainerId,
    string DockerImage,
    string? StartupCommand,
    int MemoryLimit,
    int CpuLimit,
    int DiskLimit,
    int Port,
    string IpAddress,
    ServerStatus Status,
    bool IsSuspended,
    DateTime CreatedAt,
    DateTime? LastStartedAt,
    Guid OwnerId,
    string OwnerUsername,
    Guid NodeId,
    string NodeName
)
{
    public string[]? SharedPermissions { get; init; }
    public int[] AdditionalPorts { get; init; } = [];
}

public record ServerStatsDto(
    Guid ServerId,
    ServerStatus Status,
    long MemoryUsage,
    double CpuUsage,
    long? DiskUsage,
    long BlockIoBytes,
    long NetworkRxBytes,
    long NetworkTxBytes,
    DateTime Timestamp
)
{
    public DateTime? StartedAt { get; init; }
    public long? ExitCode { get; init; }
    public bool? OomKilled { get; init; }
    public bool MetricsAvailable { get; init; }
}

// Node DTOs
public record CreateNodeRequest(
    [Required][MaxLength(100)] string Name,
    [MaxLength(500)] string? Description,
    [Required][MaxLength(255)] string Fqdn,
    [Range(1, 65535)] int? DaemonPort = 8080,
    [Range(1, 65535)] int? SftpPort = 2022,
    bool? UseSsl = false,
    bool? BehindProxy = false,
    [Range(0, int.MaxValue)] int? TotalMemory = 0,
    [Range(0, int.MaxValue)] int? TotalDisk = 0,
    [Range(0, 100)] int? MemoryOverallocate = 0,
    [Range(0, 100)] int? DiskOverallocate = 0,
    [Required][MaxLength(450)] string? DockerEndpoint = null
);

public record UpdateNodeRequest(
    string? Name,
    [MaxLength(500)] string? Description,
    string? Fqdn,
    int? DaemonPort,
    int? SftpPort,
    bool? UseSsl,
    int? TotalMemory,
    int? TotalDisk,
    int? MemoryOverallocate,
    int? DiskOverallocate
);

public record NodeDto(
    Guid Id,
    string Name,
    string Description,
    string Fqdn,
    int DaemonPort,
    int SftpPort,
    bool UseSsl,
    bool BehindProxy,
    string DockerEndpoint,
    int TotalMemory,
    int TotalDisk,
    long AllocatedMemory,
    long AllocatedDisk,
    bool IsOnline,
    bool IsMaintenanceMode,
    int ServerCount,
    DateTime CreatedAt,
    DateTime? LastCheckedAt
)
{
    public long MemoryCapacity { get; init; }
    public long DiskCapacity { get; init; }
    public int? CpuCapacityPercent { get; init; }
    public long AllocatedCpuPercent { get; init; }
}

public record CreateAllocationBulkRequest([Required][MaxLength(45)] string IpAddress, [Range(1024,65535)] int StartPort, [Range(1024,65535)] int? EndPort, [MaxLength(100)] string? Alias);

// Nest and Egg DTOs
public record NestDto(
    Guid Id,
    string Name,
    string Description,
    string Author,
    int EggCount,
    DateTime CreatedAt
);

public record EggDto(
    Guid Id,
    string Name,
    string Description,
    string DockerImage,
    string StartupCommand,
    Guid NestId,
    string NestName,
    List<EggVariableDto>? Variables,
    DateTime CreatedAt
);

public record EggVariableDto(
    string Name,
    string Description,
    string EnvVariable,
    string DefaultValue,
    bool UserViewable,
    bool UserEditable,
    string? Rules
);

// Allocation DTOs
public record CreateAllocationRequest(
    [Required] Guid NodeId,
    [Required] string IpAddress,
    [Required] int StartPort,
    int? EndPort,
    string? Alias
);

public record AllocationDto(
    Guid Id,
    string IpAddress,
    int Port,
    string? Alias,
    bool IsAssigned,
    Guid NodeId,
    Guid? ServerId,
    string? ServerName
);

// Node Connection Test
public record NodeConnectionTestResult(
    bool Success,
    string Message,
    string? DockerVersion,
    string? OperatingSystem,
    int? ContainerCount,
    long? MemoryTotal,
    long? MemoryAvailable,
    int? LogicalCpuCount = null
);

public record TestNodeConnectionRequest(
    [Required][MaxLength(500)] string DockerEndpoint
);

// Backup DTOs
public record CreateBackupRequest(
    [Required] string Name,
    List<string>? IgnoredFiles,
    bool IsLocked = false
);

public record BackupDto(
    Guid Id,
    string Name,
    long Size,
    string? Checksum,
    bool IsSuccessful,
    bool IsLocked,
    DateTime? CompletedAt,
    DateTime CreatedAt
);

// Power Action
public record PowerActionRequest(
    [Required] string Action // start, stop, restart, kill
);

// Console
public record ConsoleCommandRequest(
    [Required][MaxLength(512)] string Command
);

// Cartridge-based Server Creation
public record CreateEggServerRequest(
    [Required][MaxLength(100)] string Name,
    [MaxLength(500)] string? Description,
    [Required] Guid NodeId,
    [Range(128,1048576)] int MemoryLimit = 2048,
    [Range(1,6400)] int CpuLimit = 100,
    [Range(256,10485760)] int DiskLimit = 10240,
    [Range(1024,65535)] int? Port = null,
    Dictionary<string, object>? Variables = null,
    int[]? CpuCores = null
);

public record CreateCartridgeServerRequest(
    [Required][MaxLength(100)] string Name,
    [MaxLength(500)] string? Description,
    [Required] Guid NodeId,
    [Required] string CartridgeId,
    [Range(128,1048576)] int MemoryLimit = 2048,
    [Range(1,6400)] int CpuLimit = 100,
    [Range(256,10485760)] int DiskLimit = 10240,
    [Range(1024,65535)] int? Port = null,  // If null, auto-assign
    Dictionary<string, object>? Variables = null,
    int[]? CpuCores = null
);

// Invite Code DTOs
public record CreateInviteCodeRequest(
    [Range(1,100)] int MaxUses = 1,
    DateTime? ExpiresAt = null
);

public record InviteCodeDto(
    Guid Id,
    string Code,
    int MaxUses,
    int TimesUsed,
    DateTime? ExpiresAt,
    bool IsRevoked,
    bool IsValid,
    DateTime CreatedAt,
    string CreatedByUsername
);
