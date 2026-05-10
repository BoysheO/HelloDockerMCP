using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

[McpServerToolType]
public sealed class DockerTools
{
    private readonly DockerService _docker;

    public DockerTools(DockerService docker)
    {
        _docker = docker;
    }

    [McpServerTool]
    [Description("List Docker containers. Use this to inspect current containers and verify whether one-shot containers were auto-removed.")]
    public async Task<IReadOnlyList<object>> ListContainers()
    {
        return await _docker.ListContainersAsync();
    }

    [McpServerTool]
    [Description("Create a restricted Docker container from an allowed image. This only creates the container and does not start it. For one-shot commands, prefer run_container. After this call, use start_container with the returned name or id. Example: {\"image\":\"alpine:latest\",\"name\":\"test\",\"command\":[\"sleep\",\"60\"]}.")]
    public async Task<object> CreateContainer(
        [Description("Allowed images: nginx:alpine, redis:7-alpine, python:3.12-alpine, alpine:latest, hello-world:latest, hello-world")]
        string? image = null,

        [Description("Optional container name. If omitted, generate a unique safe name.")]
        string? name = null,

        [Description("Optional command. Recommended form is an argv array, for example [\"sleep\",\"60\"]. String form is kept for backward compatibility.")]
        JsonElement command = default,

        [Description("Memory limit in MB. Allowed range: 64-1024.")]
        long memoryMb = 256,

        [Description("CPU limit. Allowed range: >0 and <=2.")]
        double cpus = 0.5)
    {
        return await _docker.CreateContainerAsync(image, name, command, memoryMb, cpus);
    }

    [McpServerTool]
    [Description("Create and run a Docker container, wait for completion, and return stdout, stderr, and exit code. Prefer this tool for one-shot commands. It starts and waits in one call, and autoRemove defaults to true. Example: {\"image\":\"alpine:latest\",\"command\":[\"wget\",\"-S\",\"-O\",\"-\",\"http://www.baidu.com\"]}.")]
    public async Task<object> RunContainer(
        [Description("Allowed images: nginx:alpine, redis:7-alpine, python:3.12-alpine, alpine:latest, hello-world:latest, hello-world")]
        string? image = null,

        [Description("Optional container name. If omitted, generate a unique safe name.")]
        string? name = null,

        [Description("Optional command. Recommended form is an argv array. Use [\"sh\",\"-c\",\"...\"] when shell features such as pipes are needed.")]
        JsonElement command = default,

        [Description("Memory limit in MB. Allowed range: 64-1024.")]
        long memoryMb = 256,

        [Description("CPU limit. Allowed range: >0 and <=2.")]
        double cpus = 0.5,

        [Description("Wait timeout in seconds. Allowed range: 1-300.")]
        int timeoutSeconds = 30,

        [Description("Remove the container after it exits or times out. Defaults to true for one-shot tasks.")]
        bool autoRemove = true)
    {
        return await _docker.RunContainerAsync(image, name, command, memoryMb, cpus, timeoutSeconds, autoRemove);
    }

    [McpServerTool]
    [Description("Start an existing container. The container argument accepts either a container name or a container id. For one-shot commands, prefer run_container because it starts, waits, and returns logs in one call. Example: {\"container\":\"hello-world-test\"}.")]
    public async Task<object> StartContainer(
        [Description("Container name or container id.")]
        string? container = null)
    {
        return await _docker.StartContainerAsync(container);
    }

    [McpServerTool]
    [Description("Stop a Docker container. The container argument accepts either a container name or a container id. Example: {\"container\":\"96a4deb79a6d\"}.")]
    public async Task<object> StopContainer(
        [Description("Container name or container id.")]
        string? container = null)
    {
        return await _docker.StopContainerAsync(container);
    }

    [McpServerTool]
    [Description("Remove a Docker container. The container argument accepts either a container name or a container id. For one-shot commands, prefer run_container with autoRemove=true. Example: {\"container\":\"hello-world-test\",\"force\":true}.")]
    public async Task<object> RemoveContainer(
        [Description("Container name or container id.")]
        string? container = null,

        [Description("Force remove the container. Prefer false unless the container cannot be stopped normally.")]
        bool force = false)
    {
        return await _docker.RemoveContainerAsync(container, force);
    }

    [McpServerTool]
    [Description("Get stdout and stderr logs from a container. The container argument accepts either a name or id. For short-lived containers, call this after the container has exited, or use run_container instead. Maximum tail is 500 lines.")]
    public async Task<object> GetContainerLogs(
        [Description("Container name or container id.")]
        string? container = null,

        int tail = 100)
    {
        return await _docker.GetLogsAsync(container, tail);
    }

    [McpServerTool]
    [Description("Inspect a container by name or id and return state, status, exit code, startedAt, and finishedAt. Use this after create/start workflows when you need lifecycle details.")]
    public async Task<object> InspectContainer(
        [Description("Container name or container id.")]
        string? container = null)
    {
        return await _docker.InspectContainerAsync(container);
    }

    [McpServerTool]
    [Description("Make an HTTP GET or HEAD request without requiring the AI to create a Docker container or write wget commands. Use this when the user asks to test access to a URL.")]
    public async Task<object> HttpRequest(
        string? url = null,
        string method = "GET",
        int timeoutSeconds = 10,
        int maxBytes = 4096)
    {
        return await _docker.HttpRequestAsync(url, method, timeoutSeconds, maxBytes);
    }

    [McpServerTool]
    [Description("Run the Docker hello-world image as a quick health check. It creates, starts, waits, reads logs, and auto-removes the test container.")]
    public async Task<object> RunHelloWorld()
    {
        return await _docker.RunHelloWorldAsync();
    }

    [McpServerTool]
    [Description("Clean up exited containers left by AI testing. Use namePrefix, label, and olderThanSeconds to restrict what is removed. Example: {\"label\":\"created-by=mcp\",\"olderThanSeconds\":300}.")]
    public async Task<object> CleanupExitedContainers(
        string? namePrefix = null,
        string? label = "created-by=mcp",
        int olderThanSeconds = 300)
    {
        return await _docker.CleanupExitedContainersAsync(namePrefix, label, olderThanSeconds);
    }
}
