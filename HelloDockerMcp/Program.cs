using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

// var builder = Host.CreateApplicationBuilder(args);

// // MCP stdio 模式要求普通日志不要写到 stdout。
// // stdout 要留给 MCP 协议通信。
// builder.Logging.AddConsole(options =>
// {
//     options.LogToStandardErrorThreshold = LogLevel.Trace;
// });

// builder.Services.AddSingleton<DockerGuard>();
// builder.Services.AddSingleton<DockerService>();

// builder.Services
//     .AddMcpServer()
//     .WithStdioServerTransport()
//     .WithToolsFromAssembly();

// await builder.Build().RunAsync();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<McpOAuthOptions>()
    .Bind(builder.Configuration.GetSection("OAuth"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.SigningKey), "OAuth:SigningKey is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Issuer), "OAuth:Issuer is required.")
    .ValidateOnStart();
builder.Services.AddOptions<DockerOptions>()
    .Bind(builder.Configuration.GetSection("Docker"))
    .Validate(options => options.AllowedImages.Count > 0, "Docker:AllowedImages must contain at least one image.")
    .Validate(
        options => options.AllowedImages.All(image => !string.IsNullOrWhiteSpace(image)),
        "Docker:AllowedImages cannot contain blank values.")
    .Validate(
        options => options.ResourceLimits.MinMemoryMb > 0 &&
            options.ResourceLimits.MaxMemoryMb >= options.ResourceLimits.MinMemoryMb,
        "Docker:ResourceLimits memory bounds are invalid.")
    .Validate(
        options => options.ResourceLimits.MaxCpus > 0,
        "Docker:ResourceLimits:MaxCpus must be greater than 0.")
    .ValidateOnStart();
builder.Services
    .AddAuthentication(SignedBearerAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SignedBearerAuthenticationHandler>(
        SignedBearerAuthenticationHandler.SchemeName,
        options => { });
builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder(SignedBearerAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddSingleton<DockerGuard>();
builder.Services.AddSingleton<DockerService>();
builder.Services.AddSingleton<StorageService>();
builder.Services.AddHealthChecks();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();

app.MapGet("/.well-known/oauth-protected-resource/mcp", (
    HttpContext context,
    IOptions<McpOAuthOptions> options) =>
{
    var resource = $"{context.Request.Scheme}://{context.Request.Host}/mcp";

    return Results.Json(new
    {
        resource,
        authorization_servers = new[] { options.Value.Issuer.TrimEnd('/') },
        bearer_methods_supported = new[] { "header" },
        scopes_supported = new[] { options.Value.Scope },
        resource_name = options.Value.ResourceName
    });
}).AllowAnonymous();

app.MapMcp("/mcp").RequireAuthorization();

app.Run();

public sealed class McpOAuthOptions
{
    public string Issuer { get; set; } = "http://localhost:5001";

    public string SigningKey { get; set; } = string.Empty;

    public string Scope { get; set; } = "mcp";

    public string ResourceName { get; set; } = "Hello Docker MCP";
}

public sealed class SignedBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "SignedBearer";

    private readonly McpOAuthOptions _oauthOptions;

    public SignedBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<McpOAuthOptions> oauthOptions)
        : base(options, logger, encoder)
    {
        _oauthOptions = oauthOptions.Value;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = authorization["Bearer ".Length..].Trim();
        if (!TryReadJwtPayload(token, _oauthOptions.SigningKey, out var payload))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid bearer token."));
        }

        if (!payload.TryGetProperty("iss", out var issuer) ||
            !string.Equals(issuer.GetString()?.TrimEnd('/'), _oauthOptions.Issuer.TrimEnd('/'), StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid token issuer."));
        }

        if (!payload.TryGetProperty("exp", out var expiresAt) ||
            DateTimeOffset.FromUnixTimeSeconds(expiresAt.GetInt64()) <= DateTimeOffset.UtcNow)
        {
            return Task.FromResult(AuthenticateResult.Fail("Expired bearer token."));
        }

        var scope = payload.TryGetProperty("scope", out var scopeElement)
            ? scopeElement.GetString()
            : null;

        if (scope is null ||
            !scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(_oauthOptions.Scope, StringComparer.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Required scope is missing."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, payload.GetProperty("sub").GetString() ?? "unknown"),
            new("scope", scope)
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var metadata = $"{Request.Scheme}://{Request.Host}/.well-known/oauth-protected-resource/mcp";
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{metadata}\"";
        return Task.CompletedTask;
    }

    private static bool TryReadJwtPayload(string token, string signingKey, out JsonElement payload)
    {
        payload = default;
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var signingInput = $"{parts[0]}.{parts[1]}";
        var expectedSignature = Sign(signingInput, signingKey);
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(parts[2]),
            Encoding.ASCII.GetBytes(expectedSignature)))
        {
            return false;
        }

        try
        {
            using var headerDocument = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            var header = headerDocument.RootElement;
            if (!header.TryGetProperty("alg", out var alg) ||
                !string.Equals(alg.GetString(), "HS256", StringComparison.Ordinal) ||
                !header.TryGetProperty("typ", out var typ) ||
                !string.Equals(typ.GetString(), "JWT", StringComparison.Ordinal))
            {
                return false;
            }

            using var document = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            payload = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Sign(string payload, string signingKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        return Base64UrlEncode(hmac.ComputeHash(Encoding.ASCII.GetBytes(payload)));
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private static string Base64UrlEncode(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
