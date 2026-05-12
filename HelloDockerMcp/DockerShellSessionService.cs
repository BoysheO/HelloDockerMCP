using Docker.DotNet;
using Docker.DotNet.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Text;

public sealed class DockerShellSessionService : IDisposable
{
    private const int DefaultCols = 120;
    private const int DefaultRows = 30;
    private const int MaxSessions = 32;
    private const int MinIdleTimeoutSeconds = 1;
    private const int MaxIdleTimeoutSeconds = 24 * 60 * 60;
    private const int DefaultReadTimeoutMs = 1000;
    private const int DefaultMaxReadBytes = 64 * 1024;
    private const int MaxReadBytes = 1024 * 1024;
    private const int BufferMaxBytes = 10 * 1024 * 1024;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly DockerClient _client;
    private readonly ConcurrentDictionary<string, DockerShellSession> _sessions = new(StringComparer.Ordinal);
    private readonly Timer _cleanupTimer;

    public DockerShellSessionService()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        _client = string.IsNullOrWhiteSpace(dockerHost)
            ? new DockerClientConfiguration().CreateClient()
            : new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();
        _cleanupTimer = new Timer(_ => _ = ExpireIdleSessionsAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task<object> StartAsync(
        string? container,
        string? shell,
        string? workdir,
        IReadOnlyDictionary<string, string>? env,
        bool tty,
        int cols,
        int rows,
        int idleTimeoutSeconds)
    {
        try
        {
            ValidateIdleTimeout(idleTimeoutSeconds);
            if (_sessions.Values.Count(session => session.Status == "running") >= MaxSessions)
            {
                throw new DockerShellException("SESSION_LIMIT_EXCEEDED", $"The maximum shell session count of {MaxSessions} has been reached.");
            }

            var found = await GetRunningContainerAsync(container);
            var requestedShell = string.IsNullOrWhiteSpace(shell) ? "/bin/bash" : shell.Trim();
            cols = cols <= 0 ? DefaultCols : cols;
            rows = rows <= 0 ? DefaultRows : rows;

            var createResponse = await _client.Exec.ExecCreateContainerAsync(
                found.ID,
                new ContainerExecCreateParameters
                {
                    AttachStdin = true,
                    AttachStdout = true,
                    AttachStderr = true,
                    Tty = tty,
                    Cmd = BuildShellCommand(shell),
                    WorkingDir = string.IsNullOrWhiteSpace(workdir) ? null : workdir.Trim(),
                    Env = env?.Select(pair => $"{pair.Key}={pair.Value}").ToList()
                },
                CancellationToken.None);

            var stream = await _client.Exec.StartAndAttachContainerExecAsync(createResponse.ID, tty, CancellationToken.None);
            if (tty)
            {
                await ResizeExecAsync(createResponse.ID, cols, rows, CancellationToken.None);
            }

            var now = DateTimeOffset.UtcNow;
            var session = new DockerShellSession(
                sessionId: $"sess_{Guid.NewGuid():N}",
                execId: createResponse.ID,
                containerId: found.ID,
                containerName: FirstName(found),
                shell: requestedShell,
                tty: tty,
                stream: stream,
                createdAt: now,
                idleTimeoutSeconds: idleTimeoutSeconds);

            if (!_sessions.TryAdd(session.SessionId, session))
            {
                await session.CloseAsync("closed");
                throw new DockerShellException("SESSION_LIMIT_EXCEEDED", "Could not allocate a unique shell session id.");
            }

            session.StartReader(ReadLoopAsync);

            return new
            {
                ok = true,
                sessionId = session.SessionId,
                container = session.ContainerName,
                shell = session.Shell,
                status = session.Status,
                idleTimeoutSeconds = session.IdleTimeoutSeconds,
                expiresAt = session.ExpiresAt
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> WriteAsync(string? sessionId, string? input)
    {
        try
        {
            if (input is null)
            {
                throw new DockerShellException("MISSING_REQUIRED_ARGUMENT", "input is required.");
            }

            var session = GetActiveSession(sessionId, allowExited: false);
            await session.WriteAsync(Utf8NoBom.GetBytes(input));
            session.Touch();
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> ReadAsync(string? sessionId, int timeoutMs, int maxBytes)
    {
        try
        {
            var session = GetActiveSession(sessionId, allowExited: true);
            timeoutMs = timeoutMs < 0 ? DefaultReadTimeoutMs : timeoutMs;
            maxBytes = maxBytes <= 0 ? DefaultMaxReadBytes : Math.Min(maxBytes, MaxReadBytes);

            if (!session.HasBufferedOutput && session.Status == "running" && timeoutMs > 0)
            {
                await session.WaitForOutputAsync(TimeSpan.FromMilliseconds(timeoutMs));
            }

            var (stdout, stderr) = session.ReadBufferedText(maxBytes);
            await RefreshExitStatusAsync(session);
            session.Touch();

            return new
            {
                ok = true,
                stdout,
                stderr,
                status = session.Status,
                exitCode = session.ExitCode
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> CloseAsync(string? sessionId)
    {
        try
        {
            var normalized = NormalizeSessionId(sessionId);
            if (!_sessions.TryGetValue(normalized, out var session))
            {
                return new { ok = true, status = "closed" };
            }

            await TerminateAndCloseAsync(session, "closed");
            return new { ok = true, status = "closed" };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> SignalAsync(string? sessionId, string? signal)
    {
        try
        {
            var session = GetActiveSession(sessionId, allowExited: false);
            var normalizedSignal = string.IsNullOrWhiteSpace(signal) ? "SIGINT" : signal.Trim().ToUpperInvariant();
            switch (normalizedSignal)
            {
                case "SIGINT":
                    await session.WriteAsync(new byte[] { 0x03 });
                    break;
                case "EOF":
                    session.Stream.CloseWrite();
                    break;
                case "SIGTERM":
                    await KillExecProcessAsync(session, "TERM");
                    break;
                case "SIGKILL":
                    await KillExecProcessAsync(session, "KILL");
                    break;
                default:
                    throw new DockerShellException("SIGNAL_FAILED", $"Unsupported signal: {signal}.");
            }

            session.Touch();
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> ResizeAsync(string? sessionId, int cols, int rows)
    {
        try
        {
            var session = GetActiveSession(sessionId, allowExited: false);
            await ResizeExecAsync(session.ExecId, cols, rows, CancellationToken.None);
            session.Touch();
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        foreach (var pair in _sessions.ToArray())
        {
            if (_sessions.TryRemove(pair.Key, out var session))
            {
                TerminateAndCloseAsync(session, "closed").GetAwaiter().GetResult();
            }
        }

        _client.Dispose();
    }

    private async Task ReadLoopAsync(DockerShellSession session)
    {
        var buffer = new byte[8192];
        try
        {
            while (!session.IsTerminal)
            {
                var result = await session.Stream.ReadOutputAsync(buffer, 0, buffer.Length, session.CancellationToken);
                if (result.EOF)
                {
                    break;
                }

                if (result.Count <= 0)
                {
                    continue;
                }

                var target = session.Tty ? DockerOutputTarget.Stdout : result.Target.ToString().Equals("StandardError", StringComparison.OrdinalIgnoreCase)
                    ? DockerOutputTarget.Stderr
                    : DockerOutputTarget.Stdout;
                session.AppendOutput(target, buffer.AsSpan(0, result.Count));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            session.MarkTerminal("error", null);
        }
        finally
        {
            await RefreshExitStatusAsync(session);
            if (session.Status == "running")
            {
                session.MarkTerminal("exited", session.ExitCode);
            }

            await session.CloseAsync(session.Status);
        }
    }

    private async Task TerminateAndCloseAsync(DockerShellSession session, string status)
    {
        if (session.Status == "running")
        {
            try
            {
                await KillExecProcessAsync(session, "TERM");
                await Task.Delay(200);
                await RefreshExitStatusAsync(session);
                if (session.Status == "running")
                {
                    await KillExecProcessAsync(session, "KILL");
                }
            }
            catch
            {
                // Closing should still release local resources when Docker-side termination fails.
            }
        }

        await session.CloseAsync(status);
    }

    private async Task ExpireIdleSessionsAsync()
    {
        foreach (var pair in _sessions.ToArray())
        {
            var session = pair.Value;
            if (session.Status != "running" || DateTimeOffset.UtcNow <= session.ExpiresAt)
            {
                continue;
            }

            await TerminateAndCloseAsync(session, "expired");
        }
    }

    private DockerShellSession GetActiveSession(string? sessionId, bool allowExited)
    {
        var normalized = NormalizeSessionId(sessionId);
        if (!_sessions.TryGetValue(normalized, out var session))
        {
            throw new DockerShellException("SESSION_NOT_FOUND", $"Session not found: {normalized}.");
        }

        if (DateTimeOffset.UtcNow > session.ExpiresAt && session.Status == "running")
        {
            TerminateAndCloseAsync(session, "expired").GetAwaiter().GetResult();

            throw new DockerShellException("SESSION_EXPIRED", $"Session expired: {normalized}.");
        }

        if (session.Status == "closed")
        {
            throw new DockerShellException("SESSION_CLOSED", $"Session is closed: {normalized}.");
        }

        if (session.Status == "expired")
        {
            throw new DockerShellException("SESSION_EXPIRED", $"Session expired: {normalized}.");
        }

        if (!allowExited && session.Status == "exited")
        {
            throw new DockerShellException("SESSION_EXITED", $"Session exited: {normalized}.");
        }

        return session;
    }

    private async Task RefreshExitStatusAsync(DockerShellSession session)
    {
        if (session.Status != "running")
        {
            return;
        }

        try
        {
            var inspect = await _client.Exec.InspectContainerExecAsync(session.ExecId, CancellationToken.None);
            if (!inspect.Running)
            {
                session.MarkTerminal("exited", inspect.ExitCode);
            }
        }
        catch
        {
            session.MarkTerminal("error", null);
        }
    }

    private async Task KillExecProcessAsync(DockerShellSession session, string signal)
    {
        var inspect = await _client.Exec.InspectContainerExecAsync(session.ExecId, CancellationToken.None);
        if (inspect.Pid <= 0)
        {
            throw new DockerShellException("SIGNAL_FAILED", "Docker did not report a process id for this session.");
        }

        var killExec = await _client.Exec.ExecCreateContainerAsync(
            session.ContainerId,
            new ContainerExecCreateParameters
            {
                AttachStderr = true,
                AttachStdout = true,
                Tty = false,
                Cmd = new List<string> { "/bin/sh", "-c", $"kill -{signal} {inspect.Pid}" }
            },
            CancellationToken.None);
        await _client.Exec.StartContainerExecAsync(killExec.ID, CancellationToken.None);
    }

    private async Task ResizeExecAsync(string execId, int cols, int rows, CancellationToken cancellationToken)
    {
        if (cols <= 0 || rows <= 0)
        {
            throw new DockerShellException("RESIZE_FAILED", "cols and rows must be greater than 0.");
        }

        await _client.Exec.ResizeContainerExecTtyAsync(
            execId,
            new ContainerResizeParameters
            {
                Width = cols,
                Height = rows
            },
            cancellationToken);
    }

    private async Task<ContainerListResponse> GetRunningContainerAsync(string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
        {
            throw new DockerShellException("MISSING_REQUIRED_ARGUMENT", "container is required.");
        }

        var normalized = container.Trim().TrimStart('/');
        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters { All = true });
        var found = containers.FirstOrDefault(c =>
            c.ID.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
            c.Names.Any(n => string.Equals(n.TrimStart('/'), normalized, StringComparison.OrdinalIgnoreCase)));

        if (found is null)
        {
            throw new DockerShellException("CONTAINER_NOT_FOUND", $"Container not found: {normalized}.");
        }

        if (!string.Equals(found.State, "running", StringComparison.OrdinalIgnoreCase))
        {
            throw new DockerShellException("CONTAINER_NOT_RUNNING", $"Container is not running: {normalized}.");
        }

        return found;
    }

    private static IList<string> BuildShellCommand(string? shell)
    {
        if (string.IsNullOrWhiteSpace(shell) || string.Equals(shell.Trim(), "/bin/bash", StringComparison.Ordinal))
        {
            return new List<string>
            {
                "/bin/sh",
                "-lc",
                "if [ -x /bin/bash ]; then exec /bin/bash; else exec /bin/sh; fi"
            };
        }

        return new List<string> { shell.Trim() };
    }

    private static void ValidateIdleTimeout(int idleTimeoutSeconds)
    {
        if (idleTimeoutSeconds < MinIdleTimeoutSeconds || idleTimeoutSeconds > MaxIdleTimeoutSeconds)
        {
            throw new DockerShellException(
                "INVALID_IDLE_TIMEOUT",
                $"idleTimeoutSeconds must be between {MinIdleTimeoutSeconds} and {MaxIdleTimeoutSeconds}.");
        }
    }

    private static string NormalizeSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new DockerShellException("MISSING_REQUIRED_ARGUMENT", "sessionId is required.");
        }

        return sessionId.Trim();
    }

    private static object ToErrorResult(Exception ex)
    {
        var code = "DOCKER_DAEMON_UNAVAILABLE";
        var message = ex.Message;

        if (ex is DockerShellException shellException)
        {
            code = shellException.ErrorCode;
        }
        else if (ex is DockerApiException dockerApiException)
        {
            code = dockerApiException.StatusCode switch
            {
                HttpStatusCode.NotFound => "CONTAINER_NOT_FOUND",
                HttpStatusCode.Conflict => "SESSION_EXITED",
                _ => "DOCKER_DAEMON_UNAVAILABLE"
            };
        }

        return new
        {
            ok = false,
            errorCode = code,
            message,
            error = new
            {
                code,
                message
            }
        };
    }

    private static string FirstName(ContainerListResponse container)
    {
        return container.Names.FirstOrDefault()?.TrimStart('/') ?? ShortId(container.ID);
    }

    private static string ShortId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        return id[..Math.Min(12, id.Length)];
    }

    private enum DockerOutputTarget
    {
        Stdout,
        Stderr
    }

    private sealed class DockerShellSession
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly OutputBuffer _stdout = new(BufferMaxBytes);
        private readonly OutputBuffer _stderr = new(BufferMaxBytes);
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly SemaphoreSlim _outputSignal = new(0, int.MaxValue);
        private readonly object _stateLock = new();

        public DockerShellSession(
            string sessionId,
            string execId,
            string containerId,
            string containerName,
            string shell,
            bool tty,
            MultiplexedStream stream,
            DateTimeOffset createdAt,
            int idleTimeoutSeconds)
        {
            SessionId = sessionId;
            ExecId = execId;
            ContainerId = containerId;
            ContainerName = containerName;
            Shell = shell;
            Tty = tty;
            Stream = stream;
            CreatedAt = createdAt;
            LastActiveAt = createdAt;
            IdleTimeoutSeconds = idleTimeoutSeconds;
        }

        public string SessionId { get; }

        public string ExecId { get; }

        public string ContainerId { get; }

        public string ContainerName { get; }

        public string Shell { get; }

        public bool Tty { get; }

        public MultiplexedStream Stream { get; }

        public DateTimeOffset CreatedAt { get; }

        public int IdleTimeoutSeconds { get; }

        public DateTimeOffset LastActiveAt { get; private set; }

        public DateTimeOffset ExpiresAt => LastActiveAt.AddSeconds(IdleTimeoutSeconds);

        public string Status { get; private set; } = "running";

        public long? ExitCode { get; private set; }

        public CancellationToken CancellationToken => _cts.Token;

        public bool IsTerminal => Status is "closed" or "expired" or "exited" or "error";

        public bool HasBufferedOutput => _stdout.Count > 0 || _stderr.Count > 0;

        public void StartReader(Func<DockerShellSession, Task> readLoop)
        {
            _ = Task.Run(() => readLoop(this));
        }

        public async Task WriteAsync(byte[] bytes)
        {
            await _writeLock.WaitAsync();
            try
            {
                await Stream.WriteAsync(bytes, 0, bytes.Length, _cts.Token);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void AppendOutput(DockerOutputTarget target, ReadOnlySpan<byte> bytes)
        {
            if (target == DockerOutputTarget.Stderr)
            {
                _stderr.Append(bytes);
            }
            else
            {
                _stdout.Append(bytes);
            }

            _outputSignal.Release();
        }

        public async Task WaitForOutputAsync(TimeSpan timeout)
        {
            try
            {
                await _outputSignal.WaitAsync(timeout, _cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public (string stdout, string stderr) ReadBufferedText(int maxBytes)
        {
            var stdoutBytes = _stdout.Read(maxBytes);
            var remaining = Math.Max(0, maxBytes - stdoutBytes.Length);
            var stderrBytes = remaining == 0 ? Array.Empty<byte>() : _stderr.Read(remaining);
            return (Utf8NoBom.GetString(stdoutBytes), Utf8NoBom.GetString(stderrBytes));
        }

        public void Touch()
        {
            lock (_stateLock)
            {
                LastActiveAt = DateTimeOffset.UtcNow;
            }
        }

        public void MarkTerminal(string status, long? exitCode)
        {
            lock (_stateLock)
            {
                if (Status is "closed" or "expired")
                {
                    return;
                }

                Status = status;
                ExitCode = exitCode;
            }

            _outputSignal.Release();
        }

        public async Task CloseAsync(string status)
        {
            lock (_stateLock)
            {
                if (Status is "closed" or "expired")
                {
                    return;
                }

                Status = status;
            }

            _cts.Cancel();
            await _writeLock.WaitAsync();
            try
            {
                Stream.CloseWrite();
                Stream.Dispose();
            }
            catch
            {
            }
            finally
            {
                _writeLock.Release();
                _cts.Dispose();
                _outputSignal.Release();
            }
        }
    }

    private sealed class OutputBuffer
    {
        private readonly int _maxBytes;
        private readonly List<byte> _bytes = new();
        private readonly object _lock = new();

        public OutputBuffer(int maxBytes)
        {
            _maxBytes = maxBytes;
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _bytes.Count;
                }
            }
        }

        public void Append(ReadOnlySpan<byte> bytes)
        {
            lock (_lock)
            {
                _bytes.AddRange(bytes.ToArray());
                if (_bytes.Count > _maxBytes)
                {
                    _bytes.RemoveRange(0, _bytes.Count - _maxBytes);
                }
            }
        }

        public byte[] Read(int maxBytes)
        {
            lock (_lock)
            {
                var count = Math.Min(maxBytes, _bytes.Count);
                if (count == 0)
                {
                    return Array.Empty<byte>();
                }

                var result = _bytes.GetRange(0, count).ToArray();
                _bytes.RemoveRange(0, count);
                return result;
            }
        }
    }
}

public sealed class DockerShellException : Exception
{
    public DockerShellException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
