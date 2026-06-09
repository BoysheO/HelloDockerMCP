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
    private readonly SecretService _secrets;
    private readonly GitOptions _options;

    public GitService(StorageService storage, SecretService secrets, IOptions<GitOptions> options)
    {
        _storage = storage;
        _secrets = secrets;
        _options = options.Value;
    }

    public async Task<object> GetInfoAsync()
    {
        try
        {
            var version = await RunGitAsync(null, new[] { "--version" }, GitCommandContext.Empty, null);
            var sshVersion = await RunProcessAsync("ssh", new[] { "-V" }, null);
            var gitAvailable = version.ExitCode == 0;
            var sshAvailable = sshVersion.ExitCode == 0;
            return new
            {
                ok = gitAvailable && sshAvailable,
                gitAvailable,
                gitVersion = gitAvailable ? version.Stdout.Trim() : null,
                sshAvailable,
                sshVersion = sshAvailable ? (string.IsNullOrWhiteSpace(sshVersion.Stdout) ? sshVersion.Stderr : sshVersion.Stdout).Trim() : null,
                storageRootPath = _storage.RootPath,
                defaultTimeoutSeconds = DefaultTimeoutSeconds,
                maxTimeoutSeconds = MaxTimeoutSeconds,
                maxOutputBytes = MaxOutputBytes,
                operatingSystem = RuntimeInformation.OSDescription,
                sshWrapperMode = "sh",
                ssh = new
                {
                    defaultIdentity = "OpenSSH default identity from the Linux container service account, such as /root/.ssh keys, ssh config, or SSH agent.",
                    wrapperMode = "sh",
                    storagePrivateKeySupported = true,
                    knownHostsPath = ToStoragePath(KnownHostsPath)
                },
                errorCode = gitAvailable ? sshAvailable ? null : "SSH_NOT_AVAILABLE" : "GIT_NOT_AVAILABLE",
                message = gitAvailable && sshAvailable
                    ? "Git and OpenSSH are available. All Git repository paths are restricted to storage."
                    : gitAvailable
                        ? "The ssh executable is not available to the MCP service runtime."
                        : "The git executable is not available to the MCP service runtime.",
                hint = gitAvailable && sshAvailable
                    ? null
                    : "Install git and openssh-client in the service image or use an image that already contains both."
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
        string? httpUsername,
        string? httpPasswordSecretKey,
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

            using var commandContext = PrepareGitCommandContext(privateKeyPath, httpUsername, httpPasswordSecretKey);
            var result = await RunGitAsync(null, args, commandContext, timeoutSeconds);
            return ToGitResult(result, repositoryPath: ToStoragePath(destination), branch: branch, remote: null);
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> PullAsync(string? repositoryPath, string? remote, string? branch, string? privateKeyPath, string? httpUsername, string? httpPasswordSecretKey, int timeoutSeconds)
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

            using var commandContext = PrepareGitCommandContext(privateKeyPath, httpUsername, httpPasswordSecretKey);
            var result = await RunGitAsync(repository, args, commandContext, timeoutSeconds);
            return ToGitResult(result, ToStoragePath(repository), branch, remote);
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> FetchAsync(string? repositoryPath, string? remote, string? privateKeyPath, string? httpUsername, string? httpPasswordSecretKey, int timeoutSeconds)
    {
        try
        {
            var repository = await ResolveRepositoryAsync(repositoryPath, timeoutSeconds);
            var args = new List<string> { "fetch" };
            if (!string.IsNullOrWhiteSpace(remote))
            {
                args.Add(remote.Trim());
            }

            using var commandContext = PrepareGitCommandContext(privateKeyPath, httpUsername, httpPasswordSecretKey);
            var result = await RunGitAsync(repository, args, commandContext, timeoutSeconds);
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
            var result = await RunGitAsync(repository, new[] { "status", "--short", "--branch" }, GitCommandContext.Empty, timeoutSeconds);
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
            var result = await RunGitAsync(repository, args, GitCommandContext.Empty, timeoutSeconds);
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

            using var commandContext = PrepareGitCommandContext(privateKeyPath, null, null);
            var result = await RunGitAsync(repository, args, commandContext, timeoutSeconds);
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

        var probe = await RunGitAsync(repository, new[] { "rev-parse", "--is-inside-work-tree" }, GitCommandContext.Empty, timeoutSeconds);
        if (probe.ExitCode != 0 || !string.Equals(probe.Stdout.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new GitToolException("REPOSITORY_NOT_FOUND", $"Path is not a Git repository: {ToStoragePath(repository)}.");
        }

        return repository;
    }

    private GitCommandContext PrepareGitCommandContext(string? privateKeyPath, string? httpUsername, string? httpPasswordSecretKey)
    {
        var ssh = PrepareSsh(privateKeyPath);
        var askPass = PrepareAskPass(httpUsername, httpPasswordSecretKey);
        return new GitCommandContext(ssh.WrapperPath, askPass.WrapperPath, askPass.SecretValue, ssh, askPass);
    }

    private SshCommandLease PrepareSsh(string? privateKeyPath)
    {
        Directory.CreateDirectory(GitStatePath);
        var wrapperPath = Path.Combine(GitStatePath, $"ssh_{Guid.NewGuid():N}.sh");
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

        WriteSshWrapper(wrapperPath, args);
        return new SshCommandLease(wrapperPath, tempKeyPath);
    }

    private async Task<GitCommandResult> RunGitAsync(
        string? workingDirectory,
        IEnumerable<string> arguments,
        GitCommandContext commandContext,
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

        if (!string.IsNullOrWhiteSpace(commandContext.SshWrapperPath))
        {
            startInfo.Environment["GIT_SSH"] = commandContext.SshWrapperPath;
        }

        if (!string.IsNullOrWhiteSpace(commandContext.AskPassWrapperPath))
        {
            startInfo.Environment["GIT_ASKPASS"] = commandContext.AskPassWrapperPath;
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
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
                    Stdout: ScrubSecret(stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result.Text : string.Empty, commandContext.SecretToScrub),
                    Stderr: ScrubSecret(stderrTask.IsCompletedSuccessfully ? stderrTask.Result.Text : "Git command timed out.", commandContext.SecretToScrub),
                    StdoutTruncated: stdoutTask.IsCompletedSuccessfully && stdoutTask.Result.Truncated,
                    StderrTruncated: true,
                    TimedOut: true,
                    DurationMs: (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new GitCommandResult(
                process.ExitCode,
                ScrubSecret(stdout.Text, commandContext.SecretToScrub),
                ScrubSecret(stderr.Text, commandContext.SecretToScrub),
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

    private async Task<GitCommandResult> RunProcessAsync(string fileName, IEnumerable<string> arguments, int? timeoutSeconds)
    {
        var timeout = TimeSpan.FromSeconds(NormalizeTimeout(timeoutSeconds));
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return UnavailableProcessResult(fileName, startedAt);
            }

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
                return new GitCommandResult(null, string.Empty, $"{fileName} command timed out.", false, true, true, (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new GitCommandResult(process.ExitCode, stdout.Text, stderr.Text, stdout.Truncated, stderr.Truncated, false, (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return UnavailableProcessResult(fileName, startedAt);
        }
    }

    private static GitCommandResult UnavailableProcessResult(string fileName, long startedAt)
    {
        return new GitCommandResult(
            ExitCode: 127,
            Stdout: string.Empty,
            Stderr: $"{fileName} executable is not available.",
            StdoutTruncated: false,
            StderrTruncated: false,
            TimedOut: false,
            DurationMs: (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
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

    private AskPassLease PrepareAskPass(string? httpUsername, string? httpPasswordSecretKey)
    {
        if (string.IsNullOrWhiteSpace(httpPasswordSecretKey))
        {
            if (!string.IsNullOrWhiteSpace(httpUsername))
            {
                throw new GitToolException("MISSING_REQUIRED_ARGUMENT", "httpPasswordSecretKey is required when httpUsername is provided.");
            }

            return AskPassLease.Empty;
        }

        if (string.IsNullOrWhiteSpace(httpUsername))
        {
            throw new GitToolException("MISSING_REQUIRED_ARGUMENT", "httpUsername is required when httpPasswordSecretKey is provided.");
        }

        var secretValue = _secrets.GetValue(httpPasswordSecretKey);
        Directory.CreateDirectory(GitStatePath);
        var usernamePath = Path.Combine(GitStatePath, $"askpass_user_{Guid.NewGuid():N}");
        var passwordPath = Path.Combine(GitStatePath, $"askpass_pass_{Guid.NewGuid():N}");
        var wrapperPath = Path.Combine(GitStatePath, $"askpass_{Guid.NewGuid():N}.sh");
        File.WriteAllText(usernamePath, httpUsername, new UTF8Encoding(false));
        File.WriteAllText(passwordPath, secretValue, new UTF8Encoding(false));
        TrySetUserOnlyReadWrite(usernamePath);
        TrySetUserOnlyReadWrite(passwordPath);
        var script = $$"""
#!/bin/sh
case "$1" in
  *Username*) cat {{QuoteShArgument(usernamePath)}} ;;
  *Password*) cat {{QuoteShArgument(passwordPath)}} ;;
  *) cat {{QuoteShArgument(passwordPath)}} ;;
esac
""";
        File.WriteAllText(wrapperPath, script, new UTF8Encoding(false));
        TrySetUserOnlyExecute(wrapperPath);
        return new AskPassLease(wrapperPath, usernamePath, passwordPath, secretValue);
    }

    private static void WriteSshWrapper(string wrapperPath, IReadOnlyList<string> args)
    {
        var script = new StringBuilder();
        script.AppendLine("#!/bin/sh");
        script.Append("exec ssh");
        foreach (var arg in args.Skip(1))
        {
            script.Append(' ');
            script.Append(QuoteShArgument(arg));
        }

        script.AppendLine(" \"$@\"");
        File.WriteAllText(wrapperPath, script.ToString(), new UTF8Encoding(false));
        TrySetUserOnlyExecute(wrapperPath);
    }

    private static string QuoteShArgument(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'") + "'";
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
        if (!OperatingSystem.IsLinux())
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

    private static void TrySetUserOnlyExecute(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
        }
    }

    private static string ScrubSecret(string text, string? secret)
    {
        return string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
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

        if (ex is SecretToolException secretException)
        {
            return Error(secretException.ErrorCode, secretException.Message, "Use list_secret_keys to inspect available secret keys, or add the secret in /secrets.");
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

    private sealed class GitCommandContext : IDisposable
    {
        public static readonly GitCommandContext Empty = new(null, null, null, null, null);

        public GitCommandContext(string? sshWrapperPath, string? askPassWrapperPath, string? secretToScrub, SshCommandLease? ssh, AskPassLease? askPass)
        {
            SshWrapperPath = sshWrapperPath;
            AskPassWrapperPath = askPassWrapperPath;
            SecretToScrub = secretToScrub;
            Ssh = ssh;
            AskPass = askPass;
        }

        public string? SshWrapperPath { get; }

        public string? AskPassWrapperPath { get; }

        public string? SecretToScrub { get; }

        private SshCommandLease? Ssh { get; }

        private AskPassLease? AskPass { get; }

        public void Dispose()
        {
            Ssh?.Dispose();
            AskPass?.Dispose();
        }
    }

    private sealed class AskPassLease : IDisposable
    {
        public static readonly AskPassLease Empty = new(null, null, null, null);

        public AskPassLease(string? wrapperPath, string? usernamePath, string? passwordPath, string? secretValue)
        {
            WrapperPath = wrapperPath;
            UsernamePath = usernamePath;
            PasswordPath = passwordPath;
            SecretValue = secretValue;
        }

        public string? WrapperPath { get; }

        public string? SecretValue { get; }

        private string? UsernamePath { get; }

        private string? PasswordPath { get; }

        public void Dispose()
        {
            DeleteIfPresent(WrapperPath);
            DeleteIfPresent(UsernamePath);
            DeleteIfPresent(PasswordPath);
        }
    }

    private sealed class SshCommandLease : IDisposable
    {
        public SshCommandLease(string wrapperPath, string? temporaryPrivateKeyPath)
        {
            WrapperPath = wrapperPath;
            TemporaryPrivateKeyPath = temporaryPrivateKeyPath;
        }

        public string WrapperPath { get; }

        private string? TemporaryPrivateKeyPath { get; }

        public void Dispose()
        {
            if (!string.IsNullOrWhiteSpace(TemporaryPrivateKeyPath))
            {
                try
                {
                    File.Delete(TemporaryPrivateKeyPath);
                }
                catch
                {
                }
            }

            try
            {
                File.Delete(WrapperPath);
            }
            catch
            {
            }
        }
    }

    private static void DeleteIfPresent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
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
