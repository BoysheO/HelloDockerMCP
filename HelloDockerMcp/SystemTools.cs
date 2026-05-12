using ModelContextProtocol.Server;
using System.ComponentModel;

[McpServerToolType]
public sealed class SystemTools
{
    private readonly SystemService _system;

    public SystemTools(SystemService system)
    {
        _system = system;
    }

    // 查询 MCP 服务运行宿主环境的 CPU 架构，供人工开发者判断镜像架构兼容性。
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(DockerEnvironmentArchitectureResult))]
    [Description("Report the current server environment architecture, normalized to arm, x86, or another architecture name. This is a read-only system API.")]
    public object GetEnvironmentArchitecture()
    {
        return _system.GetEnvironmentArchitecture();
    }
}
