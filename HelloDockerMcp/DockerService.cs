using Docker.DotNet;
using Docker.DotNet.Models;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

public sealed class DockerService
{
    private const string CreatedByLabelName = "created-by";
    private const string CreatedByLabelValue = "mcp";

    private static readonly HttpClient HttpClient = new();

    private readonly DockerClient _client;
    private readonly DockerGuard _guard;

    public DockerService(DockerGuard guard)
    {
        _guard = guard;

        // Docker.DotNet can auto-connect to local Docker.
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        _client = string.IsNullOrWhiteSpace(dockerHost)
            ? new DockerClientConfiguration().CreateClient()
            : new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();
    }

    public async Task<IReadOnlyList<object>> ListContainersAsync()
    {
        try
        {
            var containers = await _client.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true
                });

            return containers.Select(c => new
            {
                ok = true,
                id = ShortId(c.ID),
                names = c.Names,
                name = FirstName(c),
                image = c.Image,
                state = c.State,
                status = c.Status,
                ports = c.Ports
            }).ToList<object>();
        }
        catch (Exception ex)
        {
            return new[] { Error("DOCKER_DAEMON_UNAVAILABLE", "Could not list Docker containers.", "Verify Docker is running and reachable.", ex.Message) };
        }
    }

    public async Task<object> CreateContainerAsync(
        string? image,
        string? name,
        JsonElement command,
        long memoryMb,
        double cpus)
    {
        try
        {
            var normalizedImage = ValidateImage(image);
            var containerName = string.IsNullOrWhiteSpace(name)
                ? GenerateContainerName(normalizedImage)
                : name.Trim();
            _guard.ValidateResourceLimits(memoryMb, cpus);
            var argv = ParseCommand(command);

            await PullImageAsync(normalizedImage);

            var result = await _client.Containers.CreateContainerAsync(
                CreateParameters(normalizedImage, containerName, argv, memoryMb, cpus, autoRemove: false));

            return new
            {
                ok = true,
                id = ShortId(result.ID),
                name = containerName,
                image = normalizedImage,
                command = argv,
                created = true
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> RunContainerAsync(
        string? image,
        string? name,
        JsonElement command,
        long memoryMb,
        double cpus,
        int timeoutSeconds,
        bool autoRemove)
    {
        var stopwatch = Stopwatch.StartNew();
        string? containerId = null;
        var containerName = name;
        var normalizedImage = image;
        var warnings = new List<object>();

        try
        {
            normalizedImage = ValidateImage(image);
            containerName = string.IsNullOrWhiteSpace(name)
                ? GenerateContainerName(normalizedImage)
                : name.Trim();
            _guard.ValidateResourceLimits(memoryMb, cpus);
            timeoutSeconds = ValidateTimeout(timeoutSeconds, 1, 300);
            var argv = ParseCommand(command);

            await PullImageAsync(normalizedImage);

            var createResult = await _client.Containers.CreateContainerAsync(
                CreateParameters(normalizedImage, containerName, argv, memoryMb, cpus, autoRemove: false));
            containerId = createResult.ID;

            var started = await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
            if (!started)
            {
                return new
                {
                    ok = false,
                    id = ShortId(containerId),
                    name = containerName,
                    image = normalizedImage,
                    errorCode = "CONTAINER_START_FAILED",
                    message = "Docker reported that the container did not start.",
                    hint = "Inspect the container or read logs for startup details."
                };
            }

            ContainerWaitResponse? waitResult = null;
            var timedOut = false;
            using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                try
                {
                    waitResult = await _client.Containers.WaitContainerAsync(
                        containerId,
                        timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    await TryStopContainerAsync(containerId);
                }
            }

            var logs = await ReadLogsAsync(containerId, tail: 500);
            var exitCode = timedOut ? null : waitResult?.StatusCode;
            var removeResult = await TryAutoRemoveAsync(containerId, autoRemove, warnings);
            stopwatch.Stop();

            if (timedOut)
            {
                return new
                {
                    ok = false,
                    id = ShortId(containerId),
                    name = containerName,
                    image = normalizedImage,
                    exitCode,
                    logs.stdout,
                    logs.stderr,
                    timedOut = true,
                    durationMs = stopwatch.ElapsedMilliseconds,
                    autoRemoved = removeResult,
                    warnings,
                    errorCode = "CONTAINER_TIMEOUT",
                    message = $"Container did not finish within {timeoutSeconds} seconds.",
                    hint = "Increase timeoutSeconds or inspect the container logs."
                };
            }

            var ok = exitCode == 0;
            return new
            {
                ok,
                id = ShortId(containerId),
                name = containerName,
                image = normalizedImage,
                exitCode,
                logs.stdout,
                logs.stderr,
                timedOut = false,
                durationMs = stopwatch.ElapsedMilliseconds,
                autoRemoved = removeResult,
                warnings,
                errorCode = ok ? null : "CONTAINER_EXITED_NON_ZERO",
                message = ok ? null : $"Container exited with code {exitCode}.",
                hint = ok ? null : "Read stdout and stderr to determine why the command failed."
            };
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(containerId))
            {
                await TryAutoRemoveAsync(containerId, autoRemove, warnings);
            }

            return ToErrorResult(ex, ShortId(containerId), containerName, normalizedImage, stopwatch.ElapsedMilliseconds, warnings);
        }
    }

    public async Task<object> StartContainerAsync(string? container)
    {
        try
        {
            var found = await GetContainerAsync(container);
            var started = await _client.Containers.StartContainerAsync(found.ID, new ContainerStartParameters());

            return new
            {
                ok = true,
                id = ShortId(found.ID),
                name = FirstName(found),
                container = container?.Trim(),
                started
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> StopContainerAsync(string? container)
    {
        try
        {
            var found = await GetContainerAsync(container);
            var stopped = await _client.Containers.StopContainerAsync(
                found.ID,
                new ContainerStopParameters
                {
                    WaitBeforeKillSeconds = 10
                });

            return new
            {
                ok = true,
                id = ShortId(found.ID),
                name = FirstName(found),
                container = container?.Trim(),
                stopped
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> RemoveContainerAsync(string? container, bool force = false)
    {
        try
        {
            var found = await GetContainerAsync(container);

            await _client.Containers.RemoveContainerAsync(
                found.ID,
                new ContainerRemoveParameters
                {
                    Force = force,
                    RemoveVolumes = false
                });

            return new
            {
                ok = true,
                id = ShortId(found.ID),
                name = FirstName(found),
                container = container?.Trim(),
                removed = true,
                force
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> GetLogsAsync(string? container, int tail)
    {
        try
        {
            var found = await GetContainerAsync(container);
            var logs = await ReadLogsAsync(found.ID, tail);
            var inspect = await _client.Containers.InspectContainerAsync(found.ID);

            return new
            {
                ok = true,
                id = ShortId(found.ID),
                name = FirstName(found),
                stdout = logs.stdout,
                stderr = logs.stderr,
                exitCode = inspect.State.ExitCode
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> InspectContainerAsync(string? container)
    {
        try
        {
            var found = await GetContainerAsync(container);
            var inspect = await _client.Containers.InspectContainerAsync(found.ID);

            return new
            {
                ok = true,
                id = ShortId(found.ID),
                name = inspect.Name?.TrimStart('/'),
                image = inspect.Config.Image,
                state = inspect.State.Status,
                status = found.Status,
                exitCode = inspect.State.ExitCode,
                startedAt = inspect.State.StartedAt,
                finishedAt = inspect.State.FinishedAt
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> HttpRequestAsync(string? url, string method, int timeoutSeconds, int maxBytes)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return Error(
                    "MISSING_REQUIRED_ARGUMENT",
                    "http_request requires a url.",
                    "Use {\"url\":\"http://www.baidu.com\"}.",
                    acceptedArgs: new { url = "string", method = "GET|HEAD", timeoutSeconds = "1-60", maxBytes = "0-65536" });
            }

            timeoutSeconds = ValidateTimeout(timeoutSeconds, 1, 60);
            maxBytes = Math.Clamp(maxBytes, 0, 65_536);
            var normalizedMethod = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
            if (normalizedMethod is not ("GET" or "HEAD"))
            {
                return Error("INVALID_ARGUMENT", "http_request only supports GET and HEAD.", "Use method GET or HEAD.");
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var request = new HttpRequestMessage(new HttpMethod(normalizedMethod), url);
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);

            var bytes = maxBytes == 0
                ? Array.Empty<byte>()
                : await ReadPreviewBytesAsync(response, maxBytes, timeoutCts.Token);
            var bodyPreview = Encoding.UTF8.GetString(bytes);
            var headers = response.Headers
                .Concat(response.Content.Headers)
                .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key.ToLowerInvariant(), g => string.Join(", ", g.SelectMany(h => h.Value)));
            var uri = response.RequestMessage?.RequestUri;
            var remoteIp = uri is null ? null : await ResolveRemoteIpAsync(uri.Host);

            stopwatch.Stop();

            return new
            {
                ok = response.IsSuccessStatusCode,
                url,
                statusCode = (int)response.StatusCode,
                headers,
                bodyPreview,
                remoteIp,
                durationMs = stopwatch.ElapsedMilliseconds,
                errorCode = response.IsSuccessStatusCode ? null : "HTTP_STATUS_NON_SUCCESS",
                message = response.IsSuccessStatusCode ? null : $"HTTP request returned {(int)response.StatusCode} {response.ReasonPhrase}."
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new
            {
                ok = false,
                url,
                timedOut = true,
                durationMs = stopwatch.ElapsedMilliseconds,
                errorCode = "CONTAINER_TIMEOUT",
                message = $"HTTP request did not finish within {timeoutSeconds} seconds.",
                hint = "Increase timeoutSeconds or verify that the target URL is reachable."
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> RunHelloWorldAsync()
    {
        var result = await RunContainerAsync(
            "hello-world:latest",
            name: null,
            command: default,
            memoryMb: 256,
            cpus: 0.5,
            timeoutSeconds: 30,
            autoRemove: true);

        var json = JsonSerializer.Serialize(result);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var stdout = root.TryGetProperty("stdout", out var stdoutElement) ? stdoutElement.GetString() : null;
        var ok = root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();

        return new
        {
            ok,
            message = stdout?.Contains("Hello from Docker!", StringComparison.OrdinalIgnoreCase) == true
                ? "Hello from Docker!"
                : null,
            dockerDaemonReachable = ok,
            imagePullWorks = ok,
            containerRunWorks = ok,
            stdout,
            run = result
        };
    }

    public async Task<object> CleanupExitedContainersAsync(string? namePrefix, string? label, int olderThanSeconds)
    {
        try
        {
            olderThanSeconds = Math.Clamp(olderThanSeconds, 0, 86_400);
            var filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["status"] = new Dictionary<string, bool>
                {
                    ["exited"] = true
                }
            };

            if (!string.IsNullOrWhiteSpace(label))
            {
                filters["label"] = new Dictionary<string, bool>
                {
                    [label.Trim()] = true
                };
            }

            var containers = await _client.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = filters
                });

            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-olderThanSeconds);
            var removed = new List<object>();
            var warnings = new List<object>();

            foreach (var container in containers)
            {
                var displayName = FirstName(container);
                if (!string.IsNullOrWhiteSpace(namePrefix) &&
                    !displayName.StartsWith(namePrefix.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (container.Created >= cutoff)
                {
                    continue;
                }

                try
                {
                    await _client.Containers.RemoveContainerAsync(
                        container.ID,
                        new ContainerRemoveParameters
                        {
                            Force = false,
                            RemoveVolumes = false
                        });
                    removed.Add(new { id = ShortId(container.ID), name = displayName });
                }
                catch (Exception ex)
                {
                    warnings.Add(new
                    {
                        code = "CLEANUP_REMOVE_FAILED",
                        id = ShortId(container.ID),
                        name = displayName,
                        message = ex.Message
                    });
                }
            }

            return new
            {
                ok = warnings.Count == 0,
                removed,
                count = removed.Count,
                warnings
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    private CreateContainerParameters CreateParameters(
        string image,
        string name,
        IList<string>? command,
        long memoryMb,
        double cpus,
        bool autoRemove)
    {
        return new CreateContainerParameters
        {
            Image = image,
            Name = name,
            Cmd = command,
            Labels = new Dictionary<string, string>
            {
                [CreatedByLabelName] = CreatedByLabelValue
            },
            HostConfig = new HostConfig
            {
                Memory = memoryMb * 1024L * 1024L,
                NanoCPUs = (long)(cpus * 1_000_000_000L),
                Privileged = false,
                AutoRemove = autoRemove,
                NetworkMode = "bridge",
                SecurityOpt = new List<string>
                {
                    "no-new-privileges:true"
                },
                Binds = new List<string>()
            }
        };
    }

    private async Task PullImageAsync(string image)
    {
        await _client.Images.CreateImageAsync(
            new ImagesCreateParameters
            {
                FromImage = image
            },
            authConfig: null,
            progress: new Progress<JSONMessage>());
    }

    private async Task<ContainerListResponse> GetContainerAsync(string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
        {
            throw new DockerToolException(
                "MISSING_REQUIRED_ARGUMENT",
                "A container identifier is required.",
                "Use {\"container\":\"hello-world-test\"} or {\"container\":\"96a4deb79a6d\"}.",
                new { container = "string" });
        }

        var normalized = container.Trim().TrimStart('/');
        var containers = await _client.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true
            });

        var found = containers.FirstOrDefault(c =>
            c.ID.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
            c.Names.Any(n => string.Equals(n.TrimStart('/'), normalized, StringComparison.OrdinalIgnoreCase)));

        if (found is null)
        {
            throw new DockerToolException(
                "CONTAINER_NOT_FOUND",
                $"Container not found: {normalized}.",
                "Pass a container name or id returned by list_containers, create_container, or run_container.",
                new { container = "string" });
        }

        return found;
    }

    private async Task<(string stdout, string stderr)> ReadLogsAsync(string containerId, int tail)
    {
        tail = Math.Clamp(tail, 1, 500);

        using var stream = await _client.Containers.GetContainerLogsAsync(
            containerId,
            tty: false,
            parameters: new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true,
                Tail = tail.ToString(),
                Timestamps = false,
                Follow = false
            });

        var result = await stream.ReadOutputToEndAsync(CancellationToken.None);
        return (result.stdout ?? string.Empty, result.stderr ?? string.Empty);
    }

    private async Task TryStopContainerAsync(string containerId)
    {
        try
        {
            await _client.Containers.StopContainerAsync(
                containerId,
                new ContainerStopParameters
                {
                    WaitBeforeKillSeconds = 1
                });
        }
        catch
        {
            // Timeout results should still return logs even if stop fails.
        }
    }

    private async Task<bool> TryAutoRemoveAsync(string containerId, bool autoRemove, List<object> warnings)
    {
        if (!autoRemove)
        {
            return false;
        }

        try
        {
            await _client.Containers.RemoveContainerAsync(
                containerId,
                new ContainerRemoveParameters
                {
                    Force = true,
                    RemoveVolumes = false
                });
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add(new
            {
                code = "AUTO_REMOVE_FAILED",
                message = "Container finished but could not be removed.",
                detail = ex.Message
            });
            return false;
        }
    }

    private static string ValidateImage(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            throw new DockerToolException(
                "MISSING_REQUIRED_ARGUMENT",
                "A Docker image is required.",
                "Use {\"image\":\"hello-world:latest\"} or another allowed image.",
                new { image = DockerGuard.AllowedImageList });
        }

        return image.Trim();
    }

    private static int ValidateTimeout(int timeoutSeconds, int minimum, int maximum)
    {
        if (timeoutSeconds < minimum || timeoutSeconds > maximum)
        {
            throw new DockerToolException(
                "INVALID_ARGUMENT",
                $"timeoutSeconds must be between {minimum} and {maximum}.",
                $"Use a timeoutSeconds value from {minimum} to {maximum}.");
        }

        return timeoutSeconds;
    }

    private static IList<string>? ParseCommand(JsonElement command)
    {
        if (command.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        if (command.ValueKind == JsonValueKind.String)
        {
            var value = command.GetString();
            return string.IsNullOrWhiteSpace(value)
                ? null
                : SplitCommandLine(value);
        }

        if (command.ValueKind == JsonValueKind.Array)
        {
            var args = new List<string>();
            foreach (var item in command.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new DockerToolException(
                        "COMMAND_PARSE_ERROR",
                        "Every command array item must be a string.",
                        "Use {\"command\":[\"wget\",\"-S\",\"-O\",\"-\",\"http://www.baidu.com\"]}.");
                }

                args.Add(item.GetString() ?? string.Empty);
            }

            return args;
        }

        throw new DockerToolException(
            "COMMAND_PARSE_ERROR",
            "command must be either a string or an array of strings.",
            "Prefer argv arrays such as {\"command\":[\"sh\",\"-c\",\"echo ok | head -1\"]}.");
    }

    private static IList<string> SplitCommandLine(string command)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var escaping = false;

        foreach (var ch in command)
        {
            if (escaping)
            {
                current.Append(ch);
                escaping = false;
                continue;
            }

            if (ch == '\\')
            {
                escaping = true;
                continue;
            }

            if (quote is not null)
            {
                if (ch == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (escaping)
        {
            current.Append('\\');
        }

        if (quote is not null)
        {
            throw new DockerToolException(
                "COMMAND_PARSE_ERROR",
                "Command string has an unterminated quoted string.",
                "Prefer command arrays, for example {\"command\":[\"sh\",\"-c\",\"wget -S -O - http://www.baidu.com 2>&1 | head -80\"]}.");
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    private static async Task<byte[]> ReadPreviewBytesAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(8192, maxBytes)];

        while (buffer.Length < maxBytes)
        {
            var remaining = maxBytes - (int)buffer.Length;
            var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), cancellationToken);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task<string?> ResolveRemoteIpAsync(string host)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            return addresses.FirstOrDefault()?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static object ToErrorResult(
        Exception ex,
        string? id = null,
        string? name = null,
        string? image = null,
        long? durationMs = null,
        IReadOnlyList<object>? warnings = null)
    {
        if (ex is DockerToolException toolException)
        {
            return Error(
                toolException.ErrorCode,
                toolException.Message,
                toolException.Hint,
                acceptedArgs: toolException.AcceptedArgs,
                id: id,
                name: name,
                image: image,
                durationMs: durationMs,
                warnings: warnings);
        }

        if (ex is DockerApiException dockerApiException)
        {
            var errorCode = dockerApiException.StatusCode switch
            {
                HttpStatusCode.NotFound => "CONTAINER_NOT_FOUND",
                HttpStatusCode.Conflict => "CONTAINER_ALREADY_EXISTS",
                _ => "DOCKER_DAEMON_UNAVAILABLE"
            };

            return Error(errorCode, dockerApiException.Message, "Check Docker state and retry with the corrected arguments.", id: id, name: name, image: image, durationMs: durationMs, warnings: warnings);
        }

        if (ex is InvalidOperationException invalidOperationException)
        {
            return Error("INVALID_ARGUMENT", invalidOperationException.Message, "Use the tool description and acceptedArgs to correct the request.", id: id, name: name, image: image, durationMs: durationMs, warnings: warnings);
        }

        return Error("DOCKER_DAEMON_UNAVAILABLE", ex.Message, "Verify Docker is running and reachable.", id: id, name: name, image: image, durationMs: durationMs, warnings: warnings);
    }

    private static object Error(
        string errorCode,
        string message,
        string hint,
        string? detail = null,
        object? acceptedArgs = null,
        string? id = null,
        string? name = null,
        string? image = null,
        long? durationMs = null,
        IReadOnlyList<object>? warnings = null)
    {
        return new
        {
            ok = false,
            id,
            name,
            image,
            errorCode,
            message,
            hint,
            detail,
            acceptedArgs,
            durationMs,
            warnings
        };
    }

    private static string ShortId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        return id[..Math.Min(12, id.Length)];
    }

    private static string FirstName(ContainerListResponse container)
    {
        return container.Names.FirstOrDefault()?.TrimStart('/') ?? ShortId(container.ID);
    }

    private static string GenerateContainerName(string image)
    {
        var safeImage = new string(image
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
            .ToArray())
            .Trim('-');

        var name = $"mcp-{safeImage}-{Guid.NewGuid():N}";
        return name[..Math.Min(48, name.Length)];
    }
}

public sealed class DockerToolException : Exception
{
    public DockerToolException(string errorCode, string message, string hint, object? acceptedArgs = null)
        : base(message)
    {
        ErrorCode = errorCode;
        Hint = hint;
        AcceptedArgs = acceptedArgs;
    }

    public string ErrorCode { get; }

    public string Hint { get; }

    public object? AcceptedArgs { get; }
}
