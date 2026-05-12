using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

[McpServerToolType]
public sealed class DockerTools
{
    private readonly DockerContainerService _docker;

    public DockerTools(DockerContainerService docker)
    {
        _docker = docker;
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerContainerListItemResult[]))]
    // 列出本地容器，便于人工开发者确认容器状态和自动清理结果。
    [Description("[Atomic Docker API] This API is read-only and safe to call silently. List Docker containers and inspect whether one-shot containers were auto-removed.")]
    public async Task<IReadOnlyList<object>> ListContainers()
    {
        return await _docker.ListContainersAsync();
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerCreateContainerResult))]
    // 创建容器但不启动；适合需要分步调试生命周期的场景。
    [Description("[Atomic Docker API] Create a restricted Docker container from an image in a trusted registry. This only creates the container and does not start it. For one-shot workflows, prefer run_container. Prefer specific version tags instead of latest. Optional storagePath mounts a directory inside storage into the container. Example: {\"image\":\"alpine:3.20\",\"name\":\"test\",\"command\":[\"sleep\",\"60\"],\"storagePath\":\"/\",\"containerPath\":\"/storage\"}.")]
    public async Task<object> CreateContainer(
        [Description("Docker image from a trusted registry. Prefer a specific version tag instead of latest.")]
        string? image = null,

        [Description("Optional container name. If omitted, generate a unique safe name.")]
        string? name = null,

        [Description("Optional command. Recommended form is an argv array, for example [\"sleep\",\"60\"]. String form is kept for backward compatibility.")]
        JsonElement? command = null,

        [Description("Memory limit in MB. Must be within the configured resource limit range.")]
        long memoryMb = 256,

        [Description("CPU limit. Must be greater than 0 and no more than the configured maximum.")]
        double cpus = 0.5,

        [Description("Optional directory path inside storage to mount. Use empty string or / for the storage root. When omitted, no storage directory is mounted.")]
        string? storagePath = null,

        [Description("Absolute container path where the selected storage directory is mounted. Defaults to /storage.")]
        string? containerPath = null,

        [Description("Mount the storage directory read-only.")]
        bool storageReadOnly = false)
    {
        return await _docker.CreateContainerAsync(image, name, command, memoryMb, cpus, storagePath, containerPath, storageReadOnly);
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerCreateComposeResult))]
    // 根据 compose 文本创建容器但不启动；需要一站式运行时优先用 RunContainersFromComposeYaml。
    [Description("[Atomic Docker API] Create one or more Docker containers from Docker Compose yaml text. Containers are created but not started. Images must come from trusted registries. For complete create-and-start workflows, prefer run_containers_from_compose_yaml. If the MCP client has not read the Skill yet, read it before using this complex tool.")]
    public async Task<object> CreateContainersFromComposeYaml(
        [Description("Docker Compose yaml text containing a services section. Only common fields are supported; unsupported fields are ignored.")]
        string? composeYaml = null,

        [Description("Optional project name used as a prefix when a service has no container_name.")]
        string? projectName = "mcp",

        [Description("Optional single service name to create. When omitted, all services in the yaml are created.")]
        string? serviceName = null,

        [Description("Memory limit in MB for each created container. Must be within the configured resource limit range.")]
        long memoryMb = 256,

        [Description("CPU limit for each created container. Must be greater than 0 and no more than the configured maximum.")]
        double cpus = 0.5)
    {
        return await _docker.CreateContainersFromComposeYamlAsync(composeYaml, projectName, serviceName, memoryMb, cpus);
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerRunContainerResult))]
    // 创建并运行容器，包含镜像拉取、启动、等待、日志读取和可选清理，是首选一站式接口。
    [Description("[One-shot Docker API] Pull if needed, create, run, wait, read logs, and optionally remove a Docker container. Prefer this tool for one-shot commands. Prefer specific version tags instead of latest. Optional storagePath mounts a directory inside storage. If the MCP client has not read the Skill yet, read it before using this complex tool. Example: {\"image\":\"alpine:3.20\",\"command\":[\"ls\",\"/storage\"],\"storagePath\":\"/\",\"containerPath\":\"/storage\"}.")]
    public async Task<object> RunContainer(
        [Description("Docker image from a trusted registry. Prefer a specific version tag instead of latest.")]
        string? image = null,

        [Description("Optional container name. If omitted, generate a unique safe name.")]
        string? name = null,

        [Description("Optional command. Recommended form is an argv array. Use [\"sh\",\"-c\",\"...\"] when shell features such as pipes are needed.")]
        JsonElement? command = null,

        [Description("Memory limit in MB. Must be within the configured resource limit range.")]
        long memoryMb = 256,

        [Description("CPU limit. Must be greater than 0 and no more than the configured maximum.")]
        double cpus = 0.5,

        [Description("Wait timeout in seconds. Allowed range: 1-300.")]
        int timeoutSeconds = 30,

        [Description("Remove the container after it exits or times out. Defaults to true for one-shot tasks.")]
        bool autoRemove = true,

        [Description("Optional directory path inside storage to mount. Use empty string or / for the storage root. When omitted, no storage directory is mounted.")]
        string? storagePath = null,

        [Description("Absolute container path where the selected storage directory is mounted. Defaults to /storage.")]
        string? containerPath = null,

        [Description("Mount the storage directory read-only.")]
        bool storageReadOnly = false)
    {
        return await _docker.RunContainerAsync(image, name, command, memoryMb, cpus, timeoutSeconds, autoRemove, storagePath, containerPath, storageReadOnly);
    }

    // 从单个 Dockerfile 文本构建镜像并运行容器，适合不需要准备本地文件的一站式场景。
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerBuildAndRunResult))]
    [Description("[One-shot Docker API] Build an image from a single Dockerfile text, then run a container from it. Use this when no additional local files are required. If the MCP client has not read the Skill yet, read it before using this complex tool.")]
    public async Task<object> BuildAndRunDockerfileContainer(
        [Description("Complete single-file Dockerfile text.")]
        string? dockerfile = null,

        [Description("Optional local image tag to create.")]
        string? imageTag = null,

        [Description("Optional container name. If omitted, generate a unique safe name.")]
        string? name = null,

        [Description("Optional command override.")]
        JsonElement? command = null,

        [Description("Memory limit in MB.")]
        long memoryMb = 256,

        [Description("CPU limit.")]
        double cpus = 0.5,

        [Description("Wait timeout in seconds. Allowed range: 1-300.")]
        int timeoutSeconds = 30,

        [Description("Remove the container after it exits or times out.")]
        bool autoRemove = true)
    {
        return await _docker.RunDockerfileContainerAsync(dockerfile, imageTag, name, command, memoryMb, cpus, timeoutSeconds, autoRemove);
    }

    // 从 compose 文本创建并启动容器，覆盖用户要求的 docker-compose.yml 一站式运行场景。
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerRunComposeResult))]
    [Description("[One-shot Docker API] Create and start containers from Docker Compose yaml text. Images must come from trusted registries. If the MCP client has not read the Skill yet, read it before using this complex tool.")]
    public async Task<object> RunContainersFromComposeYaml(
        [Description("Docker Compose yaml text containing a services section.")]
        string? composeYaml = null,

        [Description("Optional project name used as a prefix when a service has no container_name.")]
        string? projectName = "mcp",

        [Description("Optional single service name to run. When omitted, all services in the yaml are created and started.")]
        string? serviceName = null,

        [Description("Memory limit in MB for each created container.")]
        long memoryMb = 256,

        [Description("CPU limit for each created container.")]
        double cpus = 0.5)
    {
        return await _docker.RunContainersFromComposeYamlAsync(composeYaml, projectName, serviceName, memoryMb, cpus);
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerContainerActionResult))]
    // 启动已存在容器。通常只有调试分步生命周期时才需要。
    [Description("[Atomic Docker API] Start an existing container created for the current task or explicitly identified by the user. For one-shot commands, prefer run_container.")]
    public async Task<object> StartContainer(
        [Description("Container name or container id.")]
        string? container = null)
    {
        return await _docker.StartContainerAsync(container);
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerContainerActionResult))]
    // 停止容器，可能影响其他任务，调用前需要确认目标。
    [Description("[Atomic Docker API] Stop a Docker container. Ask the user for approval before stopping a container that was not created by the current task or explicitly identified by the user.")]
    public async Task<object> StopContainer(
        [Description("Container name or container id.")]
        string? container = null)
    {
        return await _docker.StopContainerAsync(container);
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerContainerActionResult))]
    // 删除单个容器；批量清理优先使用 RemoveContainers。
    [Description("[Atomic Docker API] Remove one Docker container. Ask the user for approval before removing a container that was not created by the current task. For one-shot commands, prefer run_container with autoRemove=true.")]
    public async Task<object> RemoveContainer(
        [Description("Container name or container id.")]
        string? container = null,

        [Description("Force remove the container. Prefer false unless the user approved force removal or normal removal failed.")]
        bool force = false)
    {
        return await _docker.RemoveContainerAsync(container, force);
    }

    // 批量删除容器，减少 MCPClient 多轮调用。
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerBatchContainerActionResult))]
    [Description("[One-shot Docker API] Remove multiple Docker containers in one call. Ask the user for approval before removing containers that were not created by the current task.")]
    public async Task<object> RemoveContainers(
        [Description("Container names or ids.")]
        IReadOnlyList<string>? containers = null,

        [Description("Force remove containers.")]
        bool force = false)
    {
        return await _docker.RemoveContainersAsync(containers, force);
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerContainerLogsResult))]
    // 获取容器日志，便于开发者定位启动失败或命令输出。
    [Description("[Atomic Docker API] Get stdout and stderr logs from a container. For short-lived containers, use run_container instead. Maximum tail is 500 lines.")]
    public async Task<object> GetContainerLogs(
        [Description("Container name or container id.")]
        string? container = null,

        [Description("Number of log lines to return from the end of the log. Maximum allowed value is 500.")]
        int tail = 100)
    {
        return await _docker.GetLogsAsync(container, tail);
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerInspectContainerResult))]
    // 查看容器详细状态，用于调试分步创建、启动流程。
    [Description("[Atomic Docker API] Inspect a container by name or id and return state, status, exit code, startedAt, and finishedAt.")]
    public async Task<object> InspectContainer(
        [Description("Container name or container id.")]
        string? container = null)
    {
        return await _docker.InspectContainerAsync(container);
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerHttpRequestResult))]
    // 不创建容器，直接由服务发起 HTTP GET/HEAD 测试。
    [Description("[Atomic Docker API] Make an HTTP GET or HEAD request without creating a Docker container. Use this when the user asks to test access to a URL.")]
    public async Task<object> HttpRequest(
        [Description("Absolute HTTP or HTTPS URL to request.")]
        string? url = null,

        [Description("HTTP method. Only GET and HEAD are supported.")]
        string method = "GET",

        [Description("Request timeout in seconds. Allowed range: 1-60.")]
        int timeoutSeconds = 10,

        [Description("Maximum response body preview bytes. Allowed range: 0-65536. Use 0 to skip reading the body.")]
        int maxBytes = 4096)
    {
        return await _docker.HttpRequestAsync(url, method, timeoutSeconds, maxBytes);
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerHelloWorldResult))]
    // 运行 hello-world 作为 Docker 连通性健康检查。
    [Description("[One-shot Docker API] Run the Docker hello-world image as a quick health check. It creates, starts, waits, reads logs, and auto-removes the test container.")]
    public async Task<object> RunHelloWorld()
    {
        return await _docker.RunHelloWorldAsync();
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerCleanupExitedContainersResult))]
    // 清理已退出容器，带过滤条件以降低误删风险。
    [Description("[One-shot Docker API] Clean up exited containers left by AI testing. Ask the user for approval before invoking this API. Use namePrefix, label, and olderThanSeconds to restrict what is removed.")]
    public async Task<object> CleanupExitedContainers(
        [Description("Optional container name prefix filter. Use this to restrict cleanup to containers from the current task.")]
        string? namePrefix = null,

        [Description("Optional Docker label filter. Defaults to created-by=mcp to avoid unrelated containers.")]
        string? label = "created-by=mcp",

        [Description("Only remove exited containers older than this many seconds. Allowed range: 0-86400.")]
        int olderThanSeconds = 300)
    {
        return await _docker.CleanupExitedContainersAsync(namePrefix, label, olderThanSeconds);
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerCleanupContainersResult))]
    // 清理所有容器，破坏性强，仅在用户明确要求时使用。
    [Description("[One-shot Docker API] Clean up all Docker containers, regardless of whether they are running. Ask the user for approval before invoking this API.")]
    public async Task<object> CleanupContainers()
    {
        return await _docker.CleanupContainersAsync();
    }
}
