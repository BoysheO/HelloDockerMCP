using System.ComponentModel;

public class ToolResult
{
    [Description("True when the operation completed successfully; false when errorCode and message describe the failure.")]
    public bool Ok { get; set; }

    [Description("Stable machine-readable error code when ok is false.")]
    public string? ErrorCode { get; set; }

    [Description("Human-readable result or error message.")]
    public string? Message { get; set; }

    [Description("Suggested corrective action when the operation fails.")]
    public string? Hint { get; set; }
}

public sealed class DockerContainerListItemResult : ToolResult
{
    public string? Id { get; set; }
    public IReadOnlyList<string>? Names { get; set; }
    public string? Name { get; set; }
    public string? Image { get; set; }
    public string? State { get; set; }
    public string? Status { get; set; }
    public object? Ports { get; set; }
}

public sealed class DockerTrustedRegistriesResult : ToolResult
{
    public IReadOnlyList<string>? TrustedRegistries { get; set; }
    public DockerResourceLimitsResult? ResourceLimits { get; set; }
}

public sealed class DockerResourceLimitsResult
{
    public NumericRangeResult? MemoryMb { get; set; }
    public NumericRangeResult? Cpus { get; set; }
}

public sealed class NumericRangeResult
{
    public double? Minimum { get; set; }
    public double? MinimumExclusive { get; set; }
    public double? Maximum { get; set; }
}

public sealed class DockerEnvironmentArchitectureResult : ToolResult
{
    public string? Architecture { get; set; }
    public string? OsArchitecture { get; set; }
    public string? ProcessArchitecture { get; set; }
    public string? OperatingSystem { get; set; }
}

public sealed class DockerCreateContainerResult : ToolResult
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Image { get; set; }
    public IReadOnlyList<string>? Command { get; set; }
    public string? StorageMount { get; set; }
    public bool? Created { get; set; }
    public object? PullProgress { get; set; }
    public object? Details { get; set; }
    public object? AcceptedArgs { get; set; }
}

public sealed class DockerCreateComposeResult : ToolResult
{
    public IReadOnlyList<DockerComposeCreatedContainerResult>? Created { get; set; }
    public int? Count { get; set; }
    public string? Note { get; set; }
    public object? Details { get; set; }
    public object? AcceptedArgs { get; set; }
}

public sealed class DockerComposeCreatedContainerResult
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Service { get; set; }
    public string? Image { get; set; }
    public IReadOnlyList<string>? Command { get; set; }
    public bool? Created { get; set; }
    public object? PullProgress { get; set; }
}

public sealed class DockerRunContainerResult : ToolResult
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Image { get; set; }
    public string? StorageMount { get; set; }
    public long? ExitCode { get; set; }
    public bool? TimedOut { get; set; }
    public long? DurationMs { get; set; }
    public bool? AutoRemoved { get; set; }
    public IReadOnlyList<object>? Warnings { get; set; }
    public object? PullProgress { get; set; }
    public object? Details { get; set; }
    public object? AcceptedArgs { get; set; }
}

public sealed class DockerContainerActionResult : ToolResult
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Container { get; set; }
    public bool? Started { get; set; }
    public bool? Stopped { get; set; }
    public bool? Removed { get; set; }
    public bool? Force { get; set; }
    public object? Details { get; set; }
    public object? AcceptedArgs { get; set; }
}

public sealed class DockerBatchContainerActionResult : ToolResult
{
    public IReadOnlyList<object>? Removed { get; set; }
    public int? Count { get; set; }
    public IReadOnlyList<object>? Warnings { get; set; }
    public bool? Force { get; set; }
}

public sealed class DockerBuildAndRunResult : ToolResult
{
    public string? Image { get; set; }
    public bool? Built { get; set; }
    public object? BuildProgress { get; set; }
    public object? Run { get; set; }
}

public sealed class DockerRunComposeResult : ToolResult
{
    public object? Created { get; set; }
    public IReadOnlyList<object>? Started { get; set; }
    public int? Count { get; set; }
    public string? Note { get; set; }
}

public sealed class DockerImageListItemResult : ToolResult
{
    public string? Id { get; set; }
    public IReadOnlyList<string>? RepoTags { get; set; }
    public IReadOnlyList<string>? RepoDigests { get; set; }
    public long? SizeBytes { get; set; }
    public DateTime? Created { get; set; }
}

public sealed class DockerImageActionResult : ToolResult
{
    public string? Image { get; set; }
    public bool? Pulled { get; set; }
    public bool? Force { get; set; }
    public object? Progress { get; set; }
    public object? Deleted { get; set; }
}

public sealed class DockerImageTagResult : ToolResult
{
    public string? SourceImage { get; set; }
    public string? Repository { get; set; }
    public string? Tag { get; set; }
    public bool? Tagged { get; set; }
}

public sealed class DockerImageBuildResult : ToolResult
{
    public string? ContextPath { get; set; }
    public string? DockerfilePath { get; set; }
    public IReadOnlyList<string>? Tags { get; set; }
    public bool? Built { get; set; }
    public object? Progress { get; set; }
}

public sealed class DockerContainerLogsResult : ToolResult
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public long? ExitCode { get; set; }
    public object? Details { get; set; }
    public object? AcceptedArgs { get; set; }
}

public sealed class DockerInspectContainerResult : ToolResult
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Image { get; set; }
    public string? State { get; set; }
    public string? Status { get; set; }
    public long? ExitCode { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public object? Details { get; set; }
    public object? AcceptedArgs { get; set; }
}

public sealed class DockerShellStartResult : ToolResult
{
    public string? SessionId { get; set; }
    public string? Container { get; set; }
    public string? Shell { get; set; }
    public string? Status { get; set; }
    public int? IdleTimeoutSeconds { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public object? Error { get; set; }
}

public sealed class DockerShellWriteResult : ToolResult
{
    public object? Error { get; set; }
}

public sealed class DockerShellReadResult : ToolResult
{
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public string? Status { get; set; }
    public long? ExitCode { get; set; }
    public object? Error { get; set; }
}

public sealed class DockerShellActionResult : ToolResult
{
    public string? Status { get; set; }
    public object? Error { get; set; }
}

public sealed class DockerHttpRequestResult : ToolResult
{
    public string? Url { get; set; }
    public int? StatusCode { get; set; }
    public IReadOnlyDictionary<string, string>? Headers { get; set; }
    public string? BodyPreview { get; set; }
    public string? RemoteIp { get; set; }
    public bool? TimedOut { get; set; }
    public long? DurationMs { get; set; }
    public object? Details { get; set; }
    public object? AcceptedArgs { get; set; }
}

public sealed class DockerHelloWorldResult : ToolResult
{
    public bool? DockerDaemonReachable { get; set; }
    public bool? ImagePullWorks { get; set; }
    public bool? ContainerRunWorks { get; set; }
    public DockerRunContainerResult? Run { get; set; }
}

public sealed class DockerCleanupExitedContainersResult : ToolResult
{
    public IReadOnlyList<DockerRemovedContainerResult>? Removed { get; set; }
    public int? Count { get; set; }
    public IReadOnlyList<DockerCleanupWarningResult>? Warnings { get; set; }
    public object? Details { get; set; }
    public object? AcceptedArgs { get; set; }
}

public sealed class DockerCleanupContainersResult : ToolResult
{
    public IReadOnlyList<DockerRemovedContainerResult>? Stopped { get; set; }
    public int? StoppedCount { get; set; }
    public IReadOnlyList<DockerRemovedContainerResult>? Removed { get; set; }
    public int? Count { get; set; }
    public IReadOnlyList<DockerCleanupWarningResult>? Warnings { get; set; }
    public object? Details { get; set; }
    public object? AcceptedArgs { get; set; }
}

public sealed class DockerRemovedContainerResult
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public bool? WasRunning { get; set; }
}

public sealed class DockerCleanupWarningResult
{
    public string? Code { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public bool? WasRunning { get; set; }
    public string? Message { get; set; }
}

public sealed class StorageInfoResult : ToolResult
{
    public string? RootPath { get; set; }
    public bool? Exists { get; set; }
    public string? Mode { get; set; }
    public string? Note { get; set; }
}

public sealed class StorageListResult : ToolResult
{
    public string? Path { get; set; }
    public bool? Recursive { get; set; }
    public string? DetailLevel { get; set; }
    public int? MaxEntries { get; set; }
    public IReadOnlyList<StorageEntryResult>? Entries { get; set; }
}

public sealed class StorageEntryResult
{
    public string? Path { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public long? SizeBytes { get; set; }
    public DateTime? LastWriteTimeUtc { get; set; }
    public string? Mode { get; set; }
}

public sealed class StorageReadTextResult : ToolResult
{
    public string? Path { get; set; }
    public string? Encoding { get; set; }
    public bool? Truncated { get; set; }
    public string? Content { get; set; }
}

public sealed class StorageWriteResult : ToolResult
{
    public string? Path { get; set; }
    public bool? CreatedOrUpdated { get; set; }
    public long? SizeBytes { get; set; }
    public string? Mode { get; set; }
}

public sealed class StorageCreateDirectoryResult : ToolResult
{
    public string? Path { get; set; }
    public bool? Created { get; set; }
    public string? Mode { get; set; }
}

public sealed class StorageMoveCopyResult : ToolResult
{
    public string? Source { get; set; }
    public string? Destination { get; set; }
    public bool? Moved { get; set; }
    public bool? Copied { get; set; }
}

public sealed class StorageDeleteResult : ToolResult
{
    public string? Path { get; set; }
    public bool? Deleted { get; set; }
}

public sealed class StorageBatchResult : ToolResult
{
    public int? Count { get; set; }
    public IReadOnlyList<object>? Results { get; set; }
}

public sealed class SkillReadResult : ToolResult
{
    public bool? Available { get; set; }
    public string? Path { get; set; }
    public string? Content { get; set; }
}

public sealed class GitInfoResult : ToolResult
{
    public bool? GitAvailable { get; set; }
    public string? GitVersion { get; set; }
    public bool? SshAvailable { get; set; }
    public string? SshVersion { get; set; }
    public string? StorageRootPath { get; set; }
    public int? DefaultTimeoutSeconds { get; set; }
    public int? MaxTimeoutSeconds { get; set; }
    public int? MaxOutputBytes { get; set; }
    public string? OperatingSystem { get; set; }
    public string? SshWrapperMode { get; set; }
    public object? Ssh { get; set; }
}

public sealed class GitCommandResult : ToolResult
{
    public string? RepositoryPath { get; set; }
    public string? Branch { get; set; }
    public string? Remote { get; set; }
    public int? ExitCode { get; set; }
    public long? DurationMs { get; set; }
    public bool? TimedOut { get; set; }
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public bool? Truncated { get; set; }
    public bool? StdoutTruncated { get; set; }
    public bool? StderrTruncated { get; set; }
}

public sealed class SecretListResult : ToolResult
{
    public int? Count { get; set; }
    public IReadOnlyList<SecretMetadataResult>? Secrets { get; set; }
}

public sealed class SecretMetadataResult
{
    public string? Key { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
