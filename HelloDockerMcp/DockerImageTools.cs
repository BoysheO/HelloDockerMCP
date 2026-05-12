using ModelContextProtocol.Server;
using System.ComponentModel;

[McpServerToolType]
public sealed class DockerImageTools
{
    private readonly DockerImageService _images;

    public DockerImageTools(DockerImageService images)
    {
        _images = images;
    }

    // 列出 Docker 本地镜像，便于人工开发者确认镜像标签、ID 和体积。
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerImageListItemResult[]))]
    [Description("[Atomic Docker API] List local Docker images. This is a read-only inspection tool and may be called silently.")]
    public async Task<IReadOnlyList<object>> ListImages()
    {
        return await _images.ListImagesAsync();
    }

    // 查询服务端信任的镜像仓库域名，MCPClient 应只在用户明确需要确认信任范围时调用。
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerTrustedRegistriesResult))]
    [Description("[Atomic Docker API] List trusted Docker registry domains and resource limits. MCP clients should call this only when the user explicitly wants to inspect trusted registries.")]
    public object ListTrustedDockerRegistries()
    {
        return _images.GetTrustedRegistries();
    }

    // 拉取或更新本地镜像，执行前会按可信域名校验镜像来源。
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerImageActionResult))]
    [Description("[Atomic Docker API] Pull or update a Docker image from a trusted registry and return pull progress. Prefer a specific version tag instead of latest.")]
    public async Task<object> PullImage(
        [Description("Docker image reference from a trusted registry. Prefer explicit version tags.")]
        string? image = null)
    {
        return await _images.PullImageAsync(image);
    }

    // 删除本地镜像。force=true 可能影响正在使用该镜像的容器，调用前应获得用户同意。
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerImageActionResult))]
    [Description("[Atomic Docker API] Delete a local Docker image by id or tag. Ask the user for approval before force deleting images.")]
    public async Task<object> RemoveImage(
        [Description("Local image id or tag.")]
        string? image = null,

        [Description("Force image deletion.")]
        bool force = false)
    {
        return await _images.RemoveImageAsync(image, force);
    }

    // 为本地镜像增加标签，不推送到远端仓库。
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerImageTagResult))]
    [Description("[Atomic Docker API] Add a Docker tag to a local image. This service does not push images to registries.")]
    public async Task<object> TagImage(
        [Description("Existing local image id or tag.")]
        string? sourceImage = null,

        [Description("Target repository name.")]
        string? repository = null,

        [Description("Target tag. Defaults to latest.")]
        string? tag = "latest")
    {
        return await _images.TagImageAsync(sourceImage, repository, tag);
    }

    // 从 storage 目录中的构建上下文创建本地镜像，调用方应先用 Storage 工具准备文件。
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerImageBuildResult))]
    [Description("[One-shot Docker API] Build a local Docker image from files already prepared under storage. The caller should first save all required files in storage, then pass contextPath and the Dockerfile path. If the MCP client has not read the Skill yet, read it before using this complex tool.")]
    public async Task<object> BuildImageFromStorage(
        [Description("Build context directory inside storage. Use / for storage root.")]
        string? contextPath = null,

        [Description("Dockerfile path relative to contextPath. Defaults to Dockerfile.")]
        string? dockerfilePath = "Dockerfile",

        [Description("Optional image tag to create. When omitted, the server generates mcp-built:<guid>.")]
        string? tag = null,

        [Description("Disable Docker layer cache when true.")]
        bool noCache = false)
    {
        return await _images.BuildImageFromStorageAsync(contextPath, dockerfilePath, tag, noCache);
    }
}
