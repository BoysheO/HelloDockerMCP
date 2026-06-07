using ModelContextProtocol.Server;
using System.ComponentModel;

[McpServerToolType]
public sealed class GitTools
{
    private readonly GitService _git;

    public GitTools(GitService git)
    {
        _git = git;
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(GitInfoResult))]
    [Description("[Atomic Git API] This API is read-only and safe to call silently. Report git availability, storage scope, SSH behavior, and configured Git command limits.")]
    public async Task<object> GetGitInfo()
    {
        return await _git.GetInfoAsync();
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(GitCommandResult))]
    [Description("[One-shot Git API] Clone a Git repository into the configured storage directory. destinationPath is relative to storage and must not already contain files. SSH uses the service runtime default identity unless privateKeyPath points to a private key file inside storage.")]
    public async Task<object> CloneGitRepository(
        [Description("Git repository URL, for example https://github.com/org/repo.git or git@github.com:org/repo.git.")]
        string? repositoryUrl = null,

        [Description("Destination directory path inside storage.")]
        string? destinationPath = null,

        [Description("Optional branch or tag to clone.")]
        string? branch = null,

        [Description("Optional shallow clone depth. Use 0 for full history.")]
        int depth = 0,

        [Description("Optional private key file path inside storage. When omitted, OpenSSH uses the service runtime default identity.")]
        string? privateKeyPath = null,

        [Description("Command timeout in seconds. Uses Git defaults when omitted or 0.")]
        int timeoutSeconds = 0)
    {
        return await _git.CloneAsync(repositoryUrl, destinationPath, branch, depth, privateKeyPath, timeoutSeconds);
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(GitCommandResult))]
    [Description("[Atomic Git API] Pull updates in an existing Git repository under storage. SSH uses the service runtime default identity unless privateKeyPath points to a private key file inside storage.")]
    public async Task<object> PullGitRepository(
        [Description("Repository directory path inside storage.")]
        string? repositoryPath = null,

        [Description("Optional remote name, for example origin.")]
        string? remote = null,

        [Description("Optional branch name. Only used when remote is also provided.")]
        string? branch = null,

        [Description("Optional private key file path inside storage. When omitted, OpenSSH uses the service runtime default identity.")]
        string? privateKeyPath = null,

        [Description("Command timeout in seconds. Uses Git defaults when omitted or 0.")]
        int timeoutSeconds = 0)
    {
        return await _git.PullAsync(repositoryPath, remote, branch, privateKeyPath, timeoutSeconds);
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(GitCommandResult))]
    [Description("[Atomic Git API] Fetch updates in an existing Git repository under storage. SSH uses the service runtime default identity unless privateKeyPath points to a private key file inside storage.")]
    public async Task<object> FetchGitRepository(
        [Description("Repository directory path inside storage.")]
        string? repositoryPath = null,

        [Description("Optional remote name, for example origin.")]
        string? remote = null,

        [Description("Optional private key file path inside storage. When omitted, OpenSSH uses the service runtime default identity.")]
        string? privateKeyPath = null,

        [Description("Command timeout in seconds. Uses Git defaults when omitted or 0.")]
        int timeoutSeconds = 0)
    {
        return await _git.FetchAsync(repositoryPath, remote, privateKeyPath, timeoutSeconds);
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(GitCommandResult))]
    [Description("[Atomic Git API] Return short status and current branch for a Git repository under storage. This is read-only and safe to call silently.")]
    public async Task<object> GetGitStatus(
        [Description("Repository directory path inside storage.")]
        string? repositoryPath = null,

        [Description("Command timeout in seconds. Uses Git defaults when omitted or 0.")]
        int timeoutSeconds = 0)
    {
        return await _git.GetStatusAsync(repositoryPath, timeoutSeconds);
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(GitCommandResult))]
    [Description("[Atomic Git API] List local branches, or local and remote branches, for a Git repository under storage. This is read-only and safe to call silently.")]
    public async Task<object> ListGitBranches(
        [Description("Repository directory path inside storage.")]
        string? repositoryPath = null,

        [Description("Include remote branches when true.")]
        bool includeRemote = false,

        [Description("Command timeout in seconds. Uses Git defaults when omitted or 0.")]
        int timeoutSeconds = 0)
    {
        return await _git.ListBranchesAsync(repositoryPath, includeRemote, timeoutSeconds);
    }

    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(GitCommandResult))]
    [Description("[Atomic Git API] Checkout an existing branch or create and checkout a new branch in a Git repository under storage. SSH uses the service runtime default identity unless privateKeyPath points to a private key file inside storage.")]
    public async Task<object> CheckoutGitBranch(
        [Description("Repository directory path inside storage.")]
        string? repositoryPath = null,

        [Description("Branch name to checkout.")]
        string? branch = null,

        [Description("Create the branch before checkout when true.")]
        bool create = false,

        [Description("Optional private key file path inside storage. When omitted, OpenSSH uses the service runtime default identity.")]
        string? privateKeyPath = null,

        [Description("Command timeout in seconds. Uses Git defaults when omitted or 0.")]
        int timeoutSeconds = 0)
    {
        return await _git.CheckoutBranchAsync(repositoryPath, branch, create, privateKeyPath, timeoutSeconds);
    }
}
