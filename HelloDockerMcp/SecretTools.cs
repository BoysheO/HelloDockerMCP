using ModelContextProtocol.Server;
using System.ComponentModel;

[McpServerToolType]
public sealed class SecretTools
{
    private readonly SecretService _secrets;

    public SecretTools(SecretService secrets)
    {
        _secrets = secrets;
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(SecretListResult))]
    [Description("[Atomic Secret API] List available secret keys and metadata only. This tool never returns plaintext secret values.")]
    public object ListSecretKeys()
    {
        return _secrets.ListForMcp();
    }
}
