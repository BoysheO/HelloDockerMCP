using ModelContextProtocol.Server;
using System.ComponentModel;

[McpServerToolType]
public sealed class DockerTools
{
    private readonly DockerService _docker;

    public DockerTools(DockerService docker)
    {
        _docker = docker;
    }

    [McpServerTool]
    [Description("List containers managed by this MCP server. Only containers with managed-by=ai-mcp are returned.")]
    public async Task<IReadOnlyList<object>> ListContainers()
    {
        return await _docker.ListManagedContainersAsync();
    }

    [McpServerTool]
    [Description("Create a restricted Docker container from an allowed image. Container name must start with ai-sandbox-.")]
    public async Task<object> CreateContainer(
        [Description("Allowed images: nginx:alpine, redis:7-alpine, python:3.12-alpine, alpine:latest, hello-world:latest, hello-world")]
        string image,

        [Description("Container name. Must start with ai-sandbox-. Example: ai-sandbox-nginx-1")]
        string name,

        [Description("Optional command. Keep simple, for example: sleep 60")]
        string? command = null,

        [Description("Memory limit in MB. Allowed range: 64-1024.")]
        long memoryMb = 256,

        [Description("CPU limit. Allowed range: >0 and <=2.")]
        double cpus = 0.5)
    {
        return await _docker.CreateContainerAsync(image, name, command, memoryMb, cpus);
    }

    [McpServerTool]
    [Description("Start a managed Docker container. The container must have managed-by=ai-mcp and name must start with ai-sandbox-.")]
    public async Task<object> StartContainer(string name)
    {
        return await _docker.StartContainerAsync(name);
    }

    [McpServerTool]
    [Description("Stop a managed Docker container. The container must have managed-by=ai-mcp and name must start with ai-sandbox-.")]
    public async Task<object> StopContainer(string name)
    {
        return await _docker.StopContainerAsync(name);
    }

    [McpServerTool]
    [Description("Remove a managed Docker container. The container must have managed-by=ai-mcp and name must start with ai-sandbox-.")]
    public async Task<object> RemoveContainer(
        string name,

        [Description("Force remove the container. Prefer false unless the container cannot be stopped normally.")]
        bool force = false)
    {
        return await _docker.RemoveContainerAsync(name, force);
    }

    [McpServerTool]
    [Description("Get logs from a managed Docker container. Maximum tail is 500 lines.")]
    public async Task<string> GetContainerLogs(string name, int tail = 100)
    {
        return await _docker.GetLogsAsync(name, tail);
    }
}
