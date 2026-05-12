using ModelContextProtocol.Server;
using System.ComponentModel;

[McpServerToolType]
public sealed class SkillTools
{
    private readonly SkillService _skill;

    public SkillTools(SkillService skill)
    {
        _skill = skill;
    }

    // 读取宿主机维护的 Skill.md，不缓存内容，保证开发者手动更新后立即生效。
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(SkillReadResult))]
    [Description("Read the host-maintained Skill.md for this MCP service. Do not call on every request; MCP clients should read it on initial connection or when the user explicitly asks.")]
    public async Task<object> ReadSkill()
    {
        return await _skill.ReadSkillAsync();
    }
}
