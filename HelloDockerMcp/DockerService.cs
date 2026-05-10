using Docker.DotNet;
using Docker.DotNet.Models;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

public sealed class DockerService
{
    private const string CreatedByLabelName = "created-by";
    private const string CreatedByLabelValue = "mcp";

    private static readonly HttpClient HttpClient = new();

    private readonly DockerClient _client;
    private readonly DockerGuard _guard;
    private readonly StorageService _storage;

    public DockerService(DockerGuard guard, StorageService storage)
    {
        _guard = guard;
        _storage = storage;

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

    public object GetAvailableImages()
    {
        return _guard.GetAvailableImages();
    }

    public object GetEnvironmentArchitecture()
    {
        return new
        {
            ok = true,
            architecture = NormalizeArchitecture(RuntimeInformation.OSArchitecture),
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            operatingSystem = RuntimeInformation.OSDescription
        };
    }

    public async Task<object> CreateContainerAsync(
        string? image,
        string? name,
        JsonElement? command,
        long memoryMb,
        double cpus,
        string? storagePath = null,
        string? containerPath = null,
        bool storageReadOnly = false)
    {
        try
        {
            var normalizedImage = ValidateImage(image);
            var containerName = string.IsNullOrWhiteSpace(name)
                ? GenerateContainerName(normalizedImage)
                : name.Trim();
            _guard.ValidateResourceLimits(memoryMb, cpus);
            var argv = ParseCommand(command);
            var storageBind = ResolveStorageDirectoryBind(storagePath, containerPath, storageReadOnly);

            await PullImageAsync(normalizedImage);

            var result = await _client.Containers.CreateContainerAsync(
                CreateParameters(normalizedImage, containerName, argv, memoryMb, cpus, autoRemove: false, storageBind: storageBind));

            return new
            {
                ok = true,
                id = ShortId(result.ID),
                name = containerName,
                image = normalizedImage,
                command = argv,
                storageMount = storageBind,
                created = true
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> CreateContainersFromComposeYamlAsync(
        string? composeYaml,
        string? projectName,
        string? serviceName,
        long memoryMb,
        double cpus)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(composeYaml))
            {
                return Error(
                    "MISSING_REQUIRED_ARGUMENT",
                    "composeYaml is required.",
                    "Pass Docker Compose yaml text with a services section.",
                    acceptedArgs: new { composeYaml = "string", projectName = "string", serviceName = "string?" });
            }

            _guard.ValidateResourceLimits(memoryMb, cpus);
            var services = ParseComposeServices(composeYaml);
            if (!string.IsNullOrWhiteSpace(serviceName))
            {
                services = services
                    .Where(service => string.Equals(service.Name, serviceName.Trim(), StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (services.Count == 0)
            {
                return Error(
                    "COMPOSE_SERVICE_NOT_FOUND",
                    "No matching compose services were found.",
                    "Verify the services section and serviceName argument.");
            }

            var created = new List<object>();
            foreach (var service in services)
            {
                if (string.IsNullOrWhiteSpace(service.Image))
                {
                    throw new DockerToolException(
                        "COMPOSE_SERVICE_MISSING_IMAGE",
                        $"Compose service '{service.Name}' does not define image.",
                        "Add an image field to each service you want this API to create.");
                }

                var normalizedImage = ValidateImage(service.Image);
                await PullImageAsync(normalizedImage);

                var containerName = string.IsNullOrWhiteSpace(service.ContainerName)
                    ? GenerateComposeContainerName(projectName, service.Name)
                    : service.ContainerName.Trim();
                var parameters = CreateParameters(
                    normalizedImage,
                    containerName,
                    service.Command,
                    memoryMb,
                    cpus,
                    autoRemove: false);

                parameters.WorkingDir = service.WorkingDir;
                parameters.Env = service.Environment.Count == 0
                    ? null
                    : service.Environment.Select(pair => $"{pair.Key}={pair.Value}").ToList();
                ApplyComposePorts(parameters, service.Ports);
                ApplyComposeVolumes(parameters, service.Volumes);

                var result = await _client.Containers.CreateContainerAsync(parameters);
                created.Add(new
                {
                    id = ShortId(result.ID),
                    name = containerName,
                    service = service.Name,
                    image = normalizedImage,
                    command = service.Command,
                    created = true
                });
            }

            return new
            {
                ok = true,
                created,
                count = created.Count,
                note = "Created from compose yaml. Containers were not started."
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
        JsonElement? command,
        long memoryMb,
        double cpus,
        int timeoutSeconds,
        bool autoRemove,
        string? storagePath = null,
        string? containerPath = null,
        bool storageReadOnly = false)
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
            var storageBind = ResolveStorageDirectoryBind(storagePath, containerPath, storageReadOnly);

            await PullImageAsync(normalizedImage);

            var createResult = await _client.Containers.CreateContainerAsync(
                CreateParameters(normalizedImage, containerName, argv, memoryMb, cpus, autoRemove: false, storageBind: storageBind));
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
                    storageMount = storageBind,
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
                storageMount = storageBind,
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
            command: null,
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

    public async Task<object> CleanupContainersAsync()
    {
        try
        {
            var containers = await _client.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true
                });

            var stopped = new List<object>();
            var removed = new List<object>();
            var warnings = new List<object>();

            foreach (var container in containers)
            {
                var displayName = FirstName(container);
                var wasRunning = string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase);

                try
                {
                    if (wasRunning)
                    {
                        await _client.Containers.StopContainerAsync(
                            container.ID,
                            new ContainerStopParameters
                            {
                                WaitBeforeKillSeconds = 10
                            });
                        stopped.Add(new { id = ShortId(container.ID), name = displayName });
                    }

                    await _client.Containers.RemoveContainerAsync(
                        container.ID,
                        new ContainerRemoveParameters
                        {
                            Force = false,
                            RemoveVolumes = false
                        });
                    removed.Add(new { id = ShortId(container.ID), name = displayName, wasRunning });
                }
                catch (Exception ex)
                {
                    warnings.Add(new
                    {
                        code = wasRunning ? "CLEANUP_STOP_OR_REMOVE_FAILED" : "CLEANUP_REMOVE_FAILED",
                        id = ShortId(container.ID),
                        name = displayName,
                        wasRunning,
                        message = ex.Message
                    });
                }
            }

            return new
            {
                ok = warnings.Count == 0,
                stopped,
                stoppedCount = stopped.Count,
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
        bool autoRemove,
        string? storageBind = null)
    {
        var binds = new List<string>();
        if (!string.IsNullOrWhiteSpace(storageBind))
        {
            binds.Add(storageBind);
        }

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
                Binds = binds
            }
        };
    }

    private string? ResolveStorageDirectoryBind(string? storagePath, string? containerPath, bool readOnly)
    {
        if (storagePath is null)
        {
            return null;
        }

        var sourcePath = ResolveStorageDirectoryPath(storagePath);
        var targetPath = ResolveContainerMountPath(containerPath);
        return $"{sourcePath}:{targetPath}{(readOnly ? ":ro" : string.Empty)}";
    }

    private string ResolveStorageDirectoryPath(string storagePath)
    {
        var rootPath = Path.GetFullPath(_storage.RootPath);
        var normalized = storagePath.Replace('\\', '/').Trim();
        var storageRootRelativePath = GetStorageRelativePathFromComposeSource(normalized);
        var relativePath = storageRootRelativePath is null
            ? normalized.TrimStart('/')
            : storageRootRelativePath.TrimStart('/');
        var fullPath = string.IsNullOrWhiteSpace(normalized) || normalized == "/"
            ? rootPath
            : Path.GetFullPath(Path.Combine(rootPath, relativePath));

        if (!IsPathInsideOrEqual(fullPath, rootPath))
        {
            throw new DockerToolException(
                "PATH_OUTSIDE_STORAGE",
                "storagePath must stay inside the configured storage directory.",
                "Use a relative path inside storage, or use empty string or / for the storage root.",
                new { storagePath = "string?" });
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DockerToolException(
                "STORAGE_DIRECTORY_NOT_FOUND",
                $"Storage directory not found: {storagePath}.",
                "Create the directory with the storage tools first, or use empty string or / for the storage root.",
                new { storagePath = "string?" });
        }

        return fullPath;
    }

    private static string ResolveContainerMountPath(string? containerPath)
    {
        var targetPath = string.IsNullOrWhiteSpace(containerPath) ? "/storage" : containerPath.Trim();
        if (!targetPath.StartsWith("/", StringComparison.Ordinal) ||
            targetPath.Contains(":", StringComparison.Ordinal) ||
            targetPath.Contains('\0'))
        {
            throw new DockerToolException(
                "INVALID_CONTAINER_PATH",
                "containerPath must be an absolute Linux container path.",
                "Use a path such as /storage or /work.",
                new { containerPath = "string?" });
        }

        return targetPath;
    }

    private static bool IsPathInsideOrEqual(string path, string rootPath)
    {
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(fullPath, fullRoot, comparison) ||
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison) ||
            fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static List<ComposeServiceDefinition> ParseComposeServices(string yaml)
    {
        var services = new List<ComposeServiceDefinition>();
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new DockerToolException(
                "INVALID_COMPOSE_YAML",
                "Compose yaml could not be parsed.",
                "Fix the yaml syntax and retry.",
                new { composeYaml = "string" }) from ex;
        }

        if (stream.Documents.Count == 0 ||
            stream.Documents[0].RootNode is not YamlMappingNode root ||
            TryGetMappingValue(root, "services") is not YamlMappingNode servicesNode)
        {
            return services;
        }

        foreach (var serviceEntry in servicesNode.Children)
        {
            var serviceName = ScalarValue(serviceEntry.Key);
            if (string.IsNullOrWhiteSpace(serviceName) ||
                serviceEntry.Value is not YamlMappingNode serviceNode)
            {
                continue;
            }

            var service = new ComposeServiceDefinition(serviceName);
            services.Add(service);

            foreach (var fieldEntry in serviceNode.Children)
            {
                var fieldName = ScalarValue(fieldEntry.Key);
                if (string.IsNullOrWhiteSpace(fieldName))
                {
                    continue;
                }

                ApplyComposeYamlNode(service, fieldName, fieldEntry.Value);
            }
        }

        return services;
    }

    private static void ApplyComposeYamlNode(ComposeServiceDefinition service, string key, YamlNode value)
    {
        switch (key)
        {
            case "image":
                service.Image = ScalarValue(value);
                break;
            case "container_name":
                service.ContainerName = ScalarValue(value);
                break;
            case "working_dir":
                service.WorkingDir = ScalarValue(value);
                break;
            case "command":
                service.Command = ParseComposeCommand(value);
                break;
            case "environment":
                ApplyComposeEnvironment(service, value);
                break;
            case "ports":
                service.Ports.AddRange(ParseComposeStringList(value));
                break;
            case "volumes":
                service.Volumes.AddRange(ParseComposeStringList(value));
                break;
        }
    }

    private static void ApplyComposeEnvironment(ComposeServiceDefinition service, YamlNode value)
    {
        if (value is YamlMappingNode environmentMap)
        {
            foreach (var item in environmentMap.Children)
            {
                var key = ScalarValue(item.Key);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    service.Environment[key] = ScalarValue(item.Value);
                }
            }

            return;
        }

        foreach (var item in ParseComposeStringList(value))
        {
            AddEnvironmentItem(service, item);
        }
    }

    private static void AddEnvironmentItem(ComposeServiceDefinition service, string item)
    {
        var separator = item.IndexOf('=');
        if (separator > 0)
        {
            service.Environment[item[..separator]] = item[(separator + 1)..];
        }
    }

    private static IList<string>? ParseComposeCommand(YamlNode value)
    {
        if (value is YamlSequenceNode commandSequence)
        {
            return commandSequence.Children
                .Select(ScalarValue)
                .Where(item => item.Length > 0)
                .ToList();
        }

        var command = ScalarValue(value);
        return string.IsNullOrWhiteSpace(command) ? null : SplitCommandLine(command);
    }

    private static List<string> ParseComposeStringList(YamlNode value)
    {
        if (value is YamlSequenceNode sequence)
        {
            return sequence.Children
                .Select(ScalarValue)
                .Where(item => item.Length > 0)
                .ToList();
        }

        var scalar = ScalarValue(value);
        return string.IsNullOrWhiteSpace(scalar) ? new List<string>() : new List<string> { scalar };
    }

    private static YamlNode? TryGetMappingValue(YamlMappingNode mapping, string key)
    {
        foreach (var item in mapping.Children)
        {
            if (string.Equals(ScalarValue(item.Key), key, StringComparison.OrdinalIgnoreCase))
            {
                return item.Value;
            }
        }

        return null;
    }

    private static string ScalarValue(YamlNode node)
    {
        return node switch
        {
            YamlScalarNode scalar => scalar.Value ?? string.Empty,
            _ => string.Empty
        };
    }

    private static void ApplyComposePorts(CreateContainerParameters parameters, IReadOnlyList<string> ports)
    {
        if (ports.Count == 0)
        {
            return;
        }

        parameters.ExposedPorts = new Dictionary<string, EmptyStruct>();
        parameters.HostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>();
        foreach (var port in ports)
        {
            var parts = port.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var hostPort = parts.Length == 1 ? null : parts[^2];
            var containerPort = parts[^1];
            if (!containerPort.Contains('/'))
            {
                containerPort += "/tcp";
            }

            parameters.ExposedPorts[containerPort] = default;
            if (!string.IsNullOrWhiteSpace(hostPort))
            {
                parameters.HostConfig.PortBindings[containerPort] = new List<PortBinding>
                {
                    new()
                    {
                        HostPort = hostPort
                    }
                };
            }
        }
    }

    private void ApplyComposeVolumes(CreateContainerParameters parameters, IReadOnlyList<string> volumes)
    {
        foreach (var volume in volumes)
        {
            if (!string.IsNullOrWhiteSpace(volume))
            {
                parameters.HostConfig.Binds.Add(NormalizeComposeStorageVolume(volume.Trim()));
            }
        }
    }

    private string NormalizeComposeStorageVolume(string volume)
    {
        var parts = volume.Split(':');
        if (parts.Length < 2 || IsLikelyWindowsDrivePath(parts))
        {
            return volume;
        }

        var source = parts[0].Trim();
        var storageRelativePath = GetStorageRelativePathFromComposeSource(source);
        if (storageRelativePath is null)
        {
            return volume;
        }

        var storageSource = ResolveStorageDirectoryPath(storageRelativePath);
        return $"{storageSource}:{string.Join(":", parts.Skip(1))}";
    }

    private string? GetStorageRelativePathFromComposeSource(string source)
    {
        var rootName = Path.GetFileName(_storage.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(rootName))
        {
            rootName = "storage";
        }

        var normalized = source.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        var storageRoot = "/" + rootName;
        if (string.Equals(normalized, rootName, StringComparison.Ordinal) ||
            string.Equals(normalized, storageRoot, StringComparison.Ordinal))
        {
            return "/";
        }

        if (normalized.StartsWith(rootName + "/", StringComparison.Ordinal))
        {
            return normalized[rootName.Length..];
        }

        if (normalized.StartsWith(storageRoot + "/", StringComparison.Ordinal))
        {
            return normalized[storageRoot.Length..];
        }

        return null;
    }

    private static bool IsLikelyWindowsDrivePath(IReadOnlyList<string> volumeParts)
    {
        return volumeParts.Count >= 3 &&
            volumeParts[0].Length == 1 &&
            char.IsLetter(volumeParts[0][0]) &&
            (volumeParts[1].StartsWith("\\", StringComparison.Ordinal) ||
                volumeParts[1].StartsWith("/", StringComparison.Ordinal));
    }

    private static string GenerateComposeContainerName(string? projectName, string serviceName)
    {
        var project = string.IsNullOrWhiteSpace(projectName) ? "mcp" : projectName.Trim();
        var safe = new string($"{project}-{serviceName}-{Guid.NewGuid():N}"
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
            .ToArray())
            .Trim('-');
        return safe[..Math.Min(48, safe.Length)];
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

    private string ValidateImage(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            throw new DockerToolException(
                "MISSING_REQUIRED_ARGUMENT",
                "A Docker image is required.",
                "Use {\"image\":\"hello-world:latest\"} or another allowed image.",
                new { image = _guard.AllowedImageList });
        }

        var normalized = image.Trim();
        _guard.ValidateImage(normalized);
        return normalized;
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

    private static string NormalizeArchitecture(Architecture architecture)
    {
        return architecture switch
        {
            Architecture.X86 or Architecture.X64 => "x86",
            Architecture.Arm or Architecture.Arm64 => "arm",
            _ => architecture.ToString().ToLowerInvariant()
        };
    }

    private static IList<string>? ParseCommand(JsonElement? command)
    {
        if (command is null || command.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        var commandValue = command.Value;

        if (commandValue.ValueKind == JsonValueKind.String)
        {
            var value = commandValue.GetString();
            return string.IsNullOrWhiteSpace(value)
                ? null
                : SplitCommandLine(value);
        }

        if (commandValue.ValueKind == JsonValueKind.Array)
        {
            var args = new List<string>();
            foreach (var item in commandValue.EnumerateArray())
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

internal sealed class ComposeServiceDefinition
{
    public ComposeServiceDefinition(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public string? Image { get; set; }

    public string? ContainerName { get; set; }

    public IList<string>? Command { get; set; }

    public string? WorkingDir { get; set; }

    public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);

    public List<string> Ports { get; } = new();

    public List<string> Volumes { get; } = new();
}
