using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Options;

public sealed class GitService
{
    private const int MinTimeoutSeconds = 1;
    private const int AbsoluteMaxTimeoutSeconds = 600;
    private const int DefaultMaxOutputBytes = 128 * 1024;

    private readonly StorageService _storage;
    private readonly GitOptions _options;

    public GitService(StorageService storage, IOptions<GitOptions> options)
    {
        _storage = storage;
        _options = options.Value;
    }

    public async Task<object> GetInfoAsync()
    {
        try
        {
            var version = await RunGitAsync(null, new[] { "--version" }, null, null);
            return new
            {
                ok = version.ExitCode == 0,
                gitAvailable = version.ExitCode == 0,
                gitVersion = version.ExitCode == 0 ? version.Stdout.Trim() : null,
                storageRootPath = _storage.RootPath,
                defaultTimeoutSeconds = DefaultTimeoutSeconds,
                maxTimeoutSeconds = MaxTimeoutSeconds,
                maxOutputBytes = MaxOutputBytes,
                ssh = new
                {
                    defaultIdentity = "OpenSSH default identity from the service runtime, such as /root/.ssh keys, ssh config, or SSH agent.",
                    storagePrivateKeySupported = true,
                    knownHostsPath = ToStoragePath(KnownHostsPath)
                },
                errorCode = version.ExitCode == 0 ? null : "GIT_NOT_AVAILABLE",
                message = version.ExitCode == 0
                    ? "Git is available. All Git repository paths are restricted to storage."
                    : "The git executable is not available to the MCP service runtime.",
                hint = version.ExitCode == 0
                    ? null
                    : "Install git in the service image or use an image that already contains git."
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> CloneAsync(
        string? repositoryUrl,
        string? destinationPath,
        string? branch,
        int depth,
        string? privateKeyPath,
        int timeoutSeconds)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(repositoryUrl))
            {
                throw new GitToolException("MISSING_REQUIRED_ARGUMENT", "repositoryUrl is required.");
            }

            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                throw new GitToolException("MISSING_REQUIRED_ARGUMENT", "destinationPath is required.");
            }

            var destination = _storage.ResolveStoragePath(destinationPath);
            if (File.Exists(destination) || Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            {
                throw new GitToolException("DESTINATION_ALREADY_EXISTS", $"Destination already exists and is not empty: {ToStoragePath(destination)}.");
            }

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var args = new List<string> { "clone" };
            if (!string.IsNullOrWhiteSpace(branch))
            {
                args.Add("--branch");
                args.Add(branch.Trim());
            }

            if (depth > 0)
            {
                args.Add("--depth");
                args.Add(depth.ToString());
            }

            args.Add(repositoryUrl.Trim());
            args.Add(destination);

            using var ssh = PrepareSsh(privateKeyPath);
            var result = await RunGitAsync(null, args, ssh.Command, timeoutSeconds);
            return ToGitResult(result, repositoryPath: ToStoragePath(destination), branch: branch, remote: null);
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> PullAsync(string? repositoryPath, string? remote, string? branch, string? privateKeyPath, int timeoutSeconds)
    {
        try
        {
            var repository = await ResolveRepositoryAsync(repositoryPath, timeoutSeconds);
            var args = new List<string> { "pull" };
            if (!string.IsNullOrWhiteSpace(remote))
            {
                args.Add(remote.Trim());
                if (!string.IsNullOrWhiteSpace(branch))
                {
                    args.Add(branch.Trim());
                }
            }

            using var ssh = PrepareSsh(privateKeyPath);
            var result = await RunGitAsync(repository, args, ssh.Command, timeoutSeconds);
            return ToGitResult(result, ToStoragePath(repository), branch, remote);
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> FetchAsync(string? repositoryPath, string? remote, string? privateKeyPath, int timeoutSeconds)
    {
        try
        {
            var repository = await ResolveRepositoryAsync(repositoryPath, timeoutSeconds);
            var args = new List<string> { "fetch" };
            if (!string.IsNullOrWhiteSpace(remote))
            {
                args.Add(remote.Trim());
            }

            using var ssh = PrepareSsh(privateKeyPath);
            var result = await RunGitAsync(repository, args, ssh.Command, timeoutSeconds);
            return ToGitResult(result, ToStoragePath(repository), branch: null, remote);
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> GetStatusAsync(string? repositoryPath, int timeoutSeconds)
    {
        try
        {
            var repository = await ResolveRepositoryAsync(repositoryPath, timeoutSeconds);
            var result = await RunGitAsync(repository, new[] { "status", "--short", "--branch" }, null, timeoutSeconds);
            return ToGitResult(result, ToStoragePath(repository), branch: null, remote: null);
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> ListBranchesAsync(string? repositoryPath, bool includeRemote, int timeoutSeconds)
    {
        try
        {
            var repository = await ResolveRepositoryAsync(repositoryPath, timeoutSeconds);
            var args = includeRemote
                ? new[] { "branch", "--all", "--verbose", "--no-abbrev" }
                : new[] { "branch", "--verbose", "--no-abbrev" };
            var result = await RunGitAsync(repository, args, null, timeoutSeconds);
            return ToGitResult(result, ToStoragePath(repository), branch: null, remote: null);
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> CheckoutBranchAsync(
        string? repositoryPath,
        string? branch,
        bool create,
        string? privateKeyPath,
        int timeoutSeconds)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(branch))
            {
                throw new GitToolException("MISSING_REQUIRED_ARGUMENT", "branch is required.");
            }

            var repository = await ResolveRepositoryAsync(repositoryPath, timeoutSeconds);
            var args = create
                ? new[] { "checkout", "-b", branch.Trim() }
                : new[] { "checkout", branch.Trim() };

            using var ssh = PrepareSsh(privateKeyPath);
            var result = await RunGitAsync(repository, args, ssh.Command, timeoutSeconds);
            return ToGitResult(result, ToStoragePath(repository), branch, remote: null);
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    private async Task<string> ResolveRepositoryAsync(string? repositoryPath, int timeoutSeconds)
    {
        var repository = _storage.ResolveStoragePath(repositoryPath);
        if (!Directory.Exists(repository))
        {
            throw new GitToolException("REPOSITORY_NOT_FOUND", $"Repository directory not found: {ToStoragePath(repository)}.");
        }

        var probe = await RunGitAsync(repository, new[] { "rev-parse", "--is-inside-work-tree" }, null, timeoutSeconds);
        if (probe.ExitCode != 0 || !string.Equals(probe.Stdout.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new GitToolException("REPOSITORY_NOT_FOUND", $"Path is not a Git repository: {ToStoragePath(repository)}.");
        }

        return repository;
    }

    private SshCommandLease PrepareSsh(string? privateKeyPath)
    {
        Directory.CreateDirectory(GitStatePath);
        var args = new List<string>
        {
            "ssh",
            "-o",
            "BatchMode=yes",
            "-o",
            $"UserKnownHostsFile={KnownHostsPath}",
            "-o",
            "StrictHostKeyChecking=accept-new"
        };

        string? tempKeyPath = null;
        if (!string.IsNullOrWhiteSpace(privateKeyPath))
        {
            var keyPath = _storage.ResolveStoragePath(privateKeyPath);
            if (!File.Exists(keyPath))
            {
                throw new GitToolException("PRIVATE_KEY_NOT_FOUND", $"Private key not found: {NormalizeStoragePath(privateKeyPath)}.");
            }

            tempKeyPath = Path.Combine(GitStatePath, $"key_{Guid.NewGuid():N}");
            using (var source = new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(tempKeyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(destination);
            }

            TrySetUserOnlyReadWrite(tempKeyPath);
            args.Add("-i");
            args.Add(tempKeyPath);
            args.Add("-o");
            args.Add("IdentitiesOnly=yes");
        }

        return new SshCommandLease(string.Join(" ", args.Select(QuoteSshCommandArgument)), tempKeyPath);
    }

    private async Task<GitCommandResult> RunGitAsync(
        string? workingDirectory,
        IEnumerable<string> arguments,
        string? gitSshCommand,
        int? timeoutSeconds)
    {
        var timeout = TimeSpan.FromSeconds(NormalizeTimeout(timeoutSeconds));
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(gitSshCommand))
        {
            startInfo.Environment["GIT_SSH_COMMAND"] = gitSshCommand;
        }

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            using var process = Process.Start(startInfo) ?? throw new GitToolException("GIT_NOT_AVAILABLE", "Could not start git.");
            using var cts = new CancellationTokenSource(timeout);
            var stdoutTask = ReadLimitedAsync(process.StandardOutput, MaxOutputBytes, cts.Token);
            var stderrTask = ReadLimitedAsync(process.StandardError, MaxOutputBytes, cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await Task.WhenAll(ObserveOutputAsync(stdoutTask), ObserveOutputAsync(stderrTask));
                return new GitCommandResult(
                    ExitCode: null,
                    Stdout: stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result.Text : string.Empty,
                    Stderr: stderrTask.IsCompletedSuccessfully ? stderrTask.Result.Text : "Git command timed out.",
                    StdoutTruncated: stdoutTask.IsCompletedSuccessfully && stdoutTask.Result.Truncated,
                    StderrTruncated: true,
                    TimedOut: true,
                    DurationMs: (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new GitCommandResult(
                process.ExitCode,
                stdout.Text,
                stderr.Text,
                stdout.Truncated,
                stderr.Truncated,
                TimedOut: false,
                DurationMs: (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GitToolException("GIT_NOT_AVAILABLE", $"The git executable is not available: {ex.Message}");
        }
    }

    private int NormalizeTimeout(int? timeoutSeconds)
    {
        var requested = timeoutSeconds.GetValueOrDefault(DefaultTimeoutSeconds);
        return Math.Clamp(requested <= 0 ? DefaultTimeoutSeconds : requested, MinTimeoutSeconds, MaxTimeoutSeconds);
    }

    private int DefaultTimeoutSeconds => Math.Clamp(_options.DefaultTimeoutSeconds <= 0 ? 60 : _options.DefaultTimeoutSeconds, MinTimeoutSeconds, AbsoluteMaxTimeoutSeconds);

    private int MaxTimeoutSeconds => Math.Clamp(_options.MaxTimeoutSeconds <= 0 ? 300 : _options.MaxTimeoutSeconds, MinTimeoutSeconds, AbsoluteMaxTimeoutSeconds);

    private int MaxOutputBytes => Math.Clamp(_options.MaxOutputBytes <= 0 ? DefaultMaxOutputBytes : _options.MaxOutputBytes, 1024, 1024 * 1024);

    private string GitStatePath => Path.Combine(_storage.RootPath, ".gitmcp");

    private string KnownHostsPath => Path.Combine(GitStatePath, "known_hosts");

    private string ToStoragePath(string fullPath)
    {
        var relative = Path.GetRelativePath(_storage.RootPath, fullPath).Replace('\\', '/');
        return relative == "." ? "/" : "/" + relative;
    }

    private static string NormalizeStoragePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Trim() == "/" || path.Trim() == ".")
        {
            return "/";
        }

        return "/" + path.Trim().Replace('\\', '/').TrimStart('/');
    }

    private static string QuoteSshCommandArgument(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static async Task<LimitedTextResult> ReadLimitedAsync(StreamReader reader, int maxChars, CancellationToken cancellationToken)
    {
        var buffer = new char[Math.Min(4096, maxChars)];
        var builder = new StringBuilder(Math.Min(maxChars, 16 * 1024));
        var truncated = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            var remaining = maxChars - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }

            if (read > remaining)
            {
                truncated = true;
            }
        }

        return new LimitedTextResult(builder.ToString(), truncated);
    }

    private static async Task ObserveOutputAsync(Task<LimitedTextResult> task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void TrySetUserOnlyReadWrite(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
        }
    }

    private static object ToGitResult(GitCommandResult result, string? repositoryPath, string? branch, string? remote)
    {
        var ok = result.ExitCode == 0 && !result.TimedOut;
        return new
        {
            ok,
            errorCode = ok ? null : result.TimedOut ? "GIT_COMMAND_TIMED_OUT" : "GIT_COMMAND_FAILED",
            message = ok ? "Git command completed." : "Git command failed.",
            repositoryPath,
            branch = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim(),
            remote = string.IsNullOrWhiteSpace(remote) ? null : remote.Trim(),
            exitCode = result.ExitCode,
            durationMs = result.DurationMs,
            timedOut = result.TimedOut,
            stdout = result.Stdout,
            stderr = result.Stderr,
            truncated = result.StdoutTruncated || result.StderrTruncated,
            stdoutTruncated = result.StdoutTruncated,
            stderrTruncated = result.StderrTruncated
        };
    }

    private static object ToErrorResult(Exception ex)
    {
        if (ex is StorageToolException storageException)
        {
            return Error(storageException.ErrorCode, storageException.Message, "Use a relative path inside the configured storage directory.");
        }

        if (ex is GitToolException gitException)
        {
            return Error(gitException.ErrorCode, gitException.Message, gitException.Hint);
        }

        return Error("GIT_COMMAND_FAILED", ex.Message, "Check the repository path, remote URL, branch, credentials, and network access.");
    }

    private static object Error(string errorCode, string message, string? hint)
    {
        return new
        {
            ok = false,
            errorCode,
            message,
            hint
        };
    }

    private sealed record LimitedTextResult(string Text, bool Truncated);

    private sealed record GitCommandResult(
        int? ExitCode,
        string Stdout,
        string Stderr,
        bool StdoutTruncated,
        bool StderrTruncated,
        bool TimedOut,
        long DurationMs);

    private sealed class SshCommandLease : IDisposable
    {
        public SshCommandLease(string command, string? temporaryPrivateKeyPath)
        {
            Command = command;
            TemporaryPrivateKeyPath = temporaryPrivateKeyPath;
        }

        public string Command { get; }

        private string? TemporaryPrivateKeyPath { get; }

        public void Dispose()
        {
            if (string.IsNullOrWhiteSpace(TemporaryPrivateKeyPath))
            {
                return;
            }

            try
            {
                File.Delete(TemporaryPrivateKeyPath);
            }
            catch
            {
            }
        }
    }
}

public sealed class GitOptions
{
    public int DefaultTimeoutSeconds { get; set; } = 60;

    public int MaxTimeoutSeconds { get; set; } = 300;

    public int MaxOutputBytes { get; set; } = 131072;
}

public sealed class GitToolException : Exception
{
    public GitToolException(string errorCode, string message, string? hint = null)
        : base(message)
    {
        ErrorCode = errorCode;
        Hint = hint;
    }

    public string ErrorCode { get; }

    public string? Hint { get; }
}
