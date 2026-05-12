using System.Text;

public sealed class SkillService
{
    private readonly string _skillPath;

    public SkillService(IConfiguration configuration)
    {
        _skillPath =
            Environment.GetEnvironmentVariable("SKILL_PATH") ??
            Environment.GetEnvironmentVariable("Skill__Path") ??
            configuration["Skill:Path"] ??
            "/skill/Skill.md";
    }

    public async Task<object> ReadSkillAsync()
    {
        var candidatePaths = new[]
        {
            _skillPath,
            Path.Combine(AppContext.BaseDirectory, "Skill.md"),
            Path.Combine(Directory.GetCurrentDirectory(), "Skill.md")
        };

        var path = candidatePaths.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return NoSkill();
        }

        var content = await File.ReadAllTextAsync(path, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(content))
        {
            return NoSkill();
        }

        return new
        {
            ok = true,
            available = true,
            path,
            content
        };
    }

    private static object NoSkill()
    {
        return new
        {
            ok = true,
            available = false,
            content = "No Skill is currently available.",
            message = "No Skill is currently available."
        };
    }
}
