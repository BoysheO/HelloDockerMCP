using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<OAuthOptions>()
    .Bind(builder.Configuration.GetSection("OAuth"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.SigningKey), "OAuth:SigningKey is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Issuer), "OAuth:Issuer is required.")
    .ValidateOnStart();
builder.Services.AddSingleton<AuthorizationCodeStore>();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");

app.MapGet("/", () => Results.Redirect("/.well-known/oauth-authorization-server"));

app.MapGet("/.well-known/oauth-authorization-server", (
    IOptions<OAuthOptions> options) =>
{
    var issuer = options.Value.Issuer.TrimEnd('/');

    return Results.Json(new
    {
        issuer,
        authorization_endpoint = $"{issuer}/authorize",
        token_endpoint = $"{issuer}/token",
        registration_endpoint = $"{issuer}/register",
        response_types_supported = new[] { "code" },
        grant_types_supported = new[] { "authorization_code" },
        token_endpoint_auth_methods_supported = new[] { "none" },
        code_challenge_methods_supported = new[] { "S256", "plain" },
        scopes_supported = new[] { "mcp" }
    });
});

app.MapGet("/authorize", (HttpRequest request) =>
{
    var parameters = request.Query.ToDictionary(
        pair => pair.Key,
        pair => pair.Value.ToString(),
        StringComparer.Ordinal);

    if (!string.Equals(parameters.GetValueOrDefault("response_type"), "code", StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "unsupported_response_type" });
    }

    if (string.IsNullOrWhiteSpace(parameters.GetValueOrDefault("client_id")) ||
        string.IsNullOrWhiteSpace(parameters.GetValueOrDefault("redirect_uri")))
    {
        return Results.BadRequest(new { error = "invalid_request" });
    }

    return Results.Content(BuildLoginPage(parameters), "text/html", Encoding.UTF8);
});

app.MapPost("/authorize", async (
    HttpRequest request,
    AuthorizationCodeStore codes,
    IOptions<OAuthOptions> options) =>
{
    var form = await request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();

    if (!options.Value.Accounts.Any(account =>
        string.Equals(account.Username, username, StringComparison.Ordinal) &&
        string.Equals(account.Password, password, StringComparison.Ordinal)))
    {
        return Results.Content(BuildLoginPage(FormToDictionary(form), "Invalid username or password."), "text/html", Encoding.UTF8);
    }

    var redirectUri = form["redirect_uri"].ToString();
    if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect))
    {
        return Results.BadRequest(new { error = "invalid_request" });
    }

    var code = codes.Create(new AuthorizationCode(
        ClientId: form["client_id"].ToString(),
        RedirectUri: redirectUri,
        CodeChallenge: EmptyToNull(form["code_challenge"].ToString()),
        CodeChallengeMethod: EmptyToNull(form["code_challenge_method"].ToString()) ?? "plain",
        Scope: EmptyToNull(form["scope"].ToString()) ?? "mcp",
        Resource: EmptyToNull(form["resource"].ToString()),
        Subject: username,
        ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(options.Value.AuthorizationCodeLifetimeSeconds)));

    var query = new Dictionary<string, string?>
    {
        ["code"] = code,
        ["state"] = EmptyToNull(form["state"].ToString())
    };

    return Results.Redirect(AppendQuery(redirect, query));
});

app.MapPost("/token", async (
    HttpRequest request,
    AuthorizationCodeStore codes,
    IOptions<OAuthOptions> options) =>
{
    var form = await request.ReadFormAsync();
    if (!string.Equals(form["grant_type"].ToString(), "authorization_code", StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "unsupported_grant_type" });
    }

    if (!codes.TryRedeem(form["code"].ToString(), out var code) ||
        code.ExpiresAt <= DateTimeOffset.UtcNow ||
        !string.Equals(code.ClientId, form["client_id"].ToString(), StringComparison.Ordinal) ||
        !string.Equals(code.RedirectUri, form["redirect_uri"].ToString(), StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "invalid_grant" });
    }

    if (!ValidateCodeVerifier(code, form["code_verifier"].ToString()))
    {
        return Results.BadRequest(new { error = "invalid_grant", error_description = "Invalid PKCE code verifier." });
    }

    var expiresIn = options.Value.AccessTokenLifetimeSeconds;
    var accessToken = JwtAccessToken.Create(
        options.Value.Issuer,
        options.Value.SigningKey,
        code.Subject,
        code.Scope,
        code.Resource,
        DateTimeOffset.UtcNow.AddSeconds(expiresIn));

    return Results.Json(new
    {
        access_token = accessToken,
        token_type = "Bearer",
        expires_in = expiresIn,
        scope = code.Scope
    });
});

app.MapPost("/register", async (HttpRequest request) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body);
    var redirectUris = body.TryGetProperty("redirect_uris", out var redirectUrisElement) &&
        redirectUrisElement.ValueKind == JsonValueKind.Array
            ? redirectUrisElement.EnumerateArray().Select(uri => uri.GetString()).Where(uri => uri is not null).ToArray()
            : Array.Empty<string?>();

    return Results.Json(new
    {
        client_id = $"client-{RandomNumberGenerator.GetHexString(16).ToLowerInvariant()}",
        client_id_issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        redirect_uris = redirectUris,
        grant_types = new[] { "authorization_code" },
        response_types = new[] { "code" },
        token_endpoint_auth_method = "none"
    }, statusCode: StatusCodes.Status201Created);
});

app.Run();

static string BuildLoginPage(IReadOnlyDictionary<string, string> parameters, string? error = null)
{
    var hiddenInputs = string.Concat(parameters.Select(pair =>
        $"<input type=\"hidden\" name=\"{Html(pair.Key)}\" value=\"{Html(pair.Value)}\" />"));
    var errorHtml = string.IsNullOrWhiteSpace(error)
        ? string.Empty
        : $"<p class=\"error\">{Html(error)}</p>";

    return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Hello Docker MCP Login</title>
  <style>
    body { margin: 0; font-family: system-ui, sans-serif; background: #f7f7f8; color: #1f2328; }
    main { width: min(360px, calc(100vw - 32px)); margin: 12vh auto 0; }
    form { display: grid; gap: 14px; padding: 24px; background: white; border: 1px solid #d8dee4; border-radius: 8px; }
    h1 { margin: 0 0 4px; font-size: 20px; line-height: 1.25; }
    label { display: grid; gap: 6px; font-size: 14px; font-weight: 600; }
    input { font: inherit; padding: 10px 12px; border: 1px solid #d0d7de; border-radius: 6px; }
    button { font: inherit; padding: 10px 12px; border: 0; border-radius: 6px; background: #0969da; color: white; font-weight: 700; }
    .error { margin: 0; color: #cf222e; font-size: 14px; }
  </style>
</head>
<body>
  <main>
    <form method="post" action="/authorize">
      <h1>Hello Docker MCP</h1>
      {{errorHtml}}
      {{hiddenInputs}}
      <label>Username <input name="username" autocomplete="username" required /></label>
      <label>Password <input name="password" type="password" autocomplete="current-password" required /></label>
      <button type="submit">Sign in</button>
    </form>
  </main>
</body>
</html>
""";
}

static Dictionary<string, string> FormToDictionary(IFormCollection form)
{
    return form
        .Where(pair => pair.Key is not "username" and not "password")
        .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
}

static string Html(string value)
{
    return System.Net.WebUtility.HtmlEncode(value);
}

static string AppendQuery(Uri uri, IReadOnlyDictionary<string, string?> query)
{
    var builder = new StringBuilder(uri.GetLeftPart(UriPartial.Path));
    var existingQuery = uri.Query.TrimStart('?');
    var pairs = new List<string>();

    if (!string.IsNullOrWhiteSpace(existingQuery))
    {
        pairs.Add(existingQuery);
    }

    pairs.AddRange(query
        .Where(pair => !string.IsNullOrEmpty(pair.Value))
        .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));

    if (pairs.Count > 0)
    {
        builder.Append('?').Append(string.Join('&', pairs));
    }

    if (!string.IsNullOrEmpty(uri.Fragment))
    {
        builder.Append(uri.Fragment);
    }

    return builder.ToString();
}

static string? EmptyToNull(string value)
{
    return string.IsNullOrWhiteSpace(value) ? null : value;
}

static bool ValidateCodeVerifier(AuthorizationCode code, string codeVerifier)
{
    if (code.CodeChallenge is null)
    {
        return true;
    }

    if (string.IsNullOrWhiteSpace(codeVerifier))
    {
        return false;
    }

    var challenge = string.Equals(code.CodeChallengeMethod, "S256", StringComparison.Ordinal)
        ? Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)))
        : codeVerifier;

    return CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(challenge),
        Encoding.ASCII.GetBytes(code.CodeChallenge));
}

static string Base64UrlEncode(byte[] value)
{
    return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class OAuthOptions
{
    public string Issuer { get; set; } = "http://localhost:5001";

    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenLifetimeSeconds { get; set; } = 3600;

    public int AuthorizationCodeLifetimeSeconds { get; set; } = 300;

    public List<AccountOptions> Accounts { get; set; } = new();
}

public sealed class AccountOptions
{
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}

public sealed record AuthorizationCode(
    string ClientId,
    string RedirectUri,
    string? CodeChallenge,
    string CodeChallengeMethod,
    string Scope,
    string? Resource,
    string Subject,
    DateTimeOffset ExpiresAt);

public sealed class AuthorizationCodeStore
{
    private readonly ConcurrentDictionary<string, AuthorizationCode> _codes = new(StringComparer.Ordinal);

    public string Create(AuthorizationCode code)
    {
        var value = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
        _codes[value] = code;
        return value;
    }

    public bool TryRedeem(string code, out AuthorizationCode authorizationCode)
    {
        return _codes.TryRemove(code, out authorizationCode!);
    }
}

public static class JwtAccessToken
{
    public static string Create(
        string issuer,
        string signingKey,
        string subject,
        string scope,
        string? audience,
        DateTimeOffset expiresAt)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(new
        {
            alg = "HS256",
            typ = "JWT"
        });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = issuer.TrimEnd('/'),
            sub = subject,
            scope,
            aud = audience,
            exp = expiresAt.ToUnixTimeSeconds(),
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
        var headerSegment = Base64UrlEncode(header);
        var payloadSegment = Base64UrlEncode(payload);
        var signingInput = $"{headerSegment}.{payloadSegment}";
        return $"{signingInput}.{Sign(signingInput, signingKey)}";
    }

    private static string Sign(string payload, string signingKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        return Base64UrlEncode(hmac.ComputeHash(Encoding.ASCII.GetBytes(payload)));
    }

    private static string Base64UrlEncode(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
