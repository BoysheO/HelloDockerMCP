using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

const string OAuthRateLimitPolicy = "OAuth";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<OAuthOptions>()
    .Bind(builder.Configuration.GetSection("OAuth"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.SigningKey), "OAuth:SigningKey is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Issuer), "OAuth:Issuer is required.")
    .ValidateOnStart();
builder.Services.AddOptions<AuthRateLimitOptions>()
    .Bind(builder.Configuration.GetSection("RateLimit"))
    .Validate(options => options.PermitLimit > 0, "RateLimit:PermitLimit must be greater than zero.")
    .Validate(options => options.WindowSeconds > 0, "RateLimit:WindowSeconds must be greater than zero.")
    .Validate(options => options.QueueLimit >= 0, "RateLimit:QueueLimit must be zero or greater.")
    .ValidateOnStart();
builder.Services.AddOptions<PasskeyOptions>()
    .Bind(builder.Configuration.GetSection("Passkeys"))
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.RelyingPartyName), "Passkeys:RelyingPartyName is required when passkeys are enabled.")
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.CredentialStorePath), "Passkeys:CredentialStorePath is required when passkeys are enabled.")
    .ValidateOnStart();
builder.Services.AddSingleton<AuthorizationCodeStore>();
builder.Services.AddSingleton<RefreshTokenStore>();
builder.Services.AddSingleton<PasskeyChallengeStore>();
builder.Services.AddSingleton<PasskeyCredentialStore>();
builder.Services.AddHealthChecks();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedHost |
        ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(OAuthRateLimitPolicy, context =>
    {
        var rateLimitOptions = context.RequestServices.GetRequiredService<IOptions<AuthRateLimitOptions>>().Value;
        return RateLimitPartition.GetFixedWindowLimiter(GetRateLimitPartitionKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimitOptions.PermitLimit,
            Window = TimeSpan.FromSeconds(rateLimitOptions.WindowSeconds),
            QueueLimit = rateLimitOptions.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    });
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseRateLimiter();

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
        grant_types_supported = new[] { "authorization_code", "refresh_token" },
        token_endpoint_auth_methods_supported = new[] { "none" },
        code_challenge_methods_supported = new[] { "S256", "plain" },
        scopes_supported = new[] { "mcp" }
    });
}).RequireRateLimiting(OAuthRateLimitPolicy);

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
}).RequireRateLimiting(OAuthRateLimitPolicy);

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

    return CreateAuthorizationRedirect(FormToDictionary(form), username, codes, options.Value);
}).RequireRateLimiting(OAuthRateLimitPolicy);

app.MapPost("/passkeys/register/options", async (
    HttpRequest request,
    PasskeyChallengeStore challenges,
    PasskeyCredentialStore credentials,
    IOptions<OAuthOptions> oauthOptions,
    IOptions<PasskeyOptions> passkeyOptions) =>
{
    if (!passkeyOptions.Value.Enabled)
    {
        return Results.NotFound();
    }

    var form = await request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();

    if (!oauthOptions.Value.Accounts.Any(account =>
        string.Equals(account.Username, username, StringComparison.Ordinal) &&
        string.Equals(account.Password, password, StringComparison.Ordinal)))
    {
        return Results.BadRequest(new { error = "invalid_grant", error_description = "Invalid username or password." });
    }

    var credential = credentials.GetByUsername(username);
    var options = PasskeyWebAuthn.CreateRegistrationOptions(
        request,
        passkeyOptions.Value,
        username,
        credential?.CredentialId);

    challenges.Create(options.Challenge, new PasskeyChallenge(
        PasskeyChallengeKind.Registration,
        username,
        FormToDictionary(form),
        DateTimeOffset.UtcNow.AddMinutes(5)));

    return Results.Json(options.Response);
}).RequireRateLimiting(OAuthRateLimitPolicy);

app.MapPost("/passkeys/register/complete", async (
    HttpRequest request,
    PasskeyChallengeStore challenges,
    PasskeyCredentialStore credentials,
    IOptions<PasskeyOptions> options) =>
{
    if (!options.Value.Enabled)
    {
        return Results.NotFound();
    }

    var result = await JsonSerializer.DeserializeAsync<PasskeyRegistrationResult>(request.Body);
    if (result is null ||
        !challenges.TryRedeem(result.Challenge, out var challenge) ||
        challenge.Kind is not PasskeyChallengeKind.Registration ||
        challenge.ExpiresAt <= DateTimeOffset.UtcNow)
    {
        return Results.BadRequest(new { error = "invalid_request" });
    }

    if (!PasskeyWebAuthn.TryCompleteRegistration(
        request,
        options.Value,
        challenge.Username,
        result,
        out var credential,
        out var error))
    {
        return Results.BadRequest(new { error = "invalid_passkey", error_description = error });
    }

    credentials.Save(credential);
    return Results.Json(new { ok = true });
}).RequireRateLimiting(OAuthRateLimitPolicy);

app.MapPost("/passkeys/login/options", async (
    HttpRequest request,
    PasskeyChallengeStore challenges,
    PasskeyCredentialStore credentials,
    IOptions<PasskeyOptions> options) =>
{
    if (!options.Value.Enabled)
    {
        return Results.NotFound();
    }

    var form = await request.ReadFormAsync();
    var username = form["username"].ToString();
    var credential = credentials.GetByUsername(username);
    if (credential is null)
    {
        return Results.BadRequest(new { error = "invalid_request", error_description = "No passkey is registered for this user." });
    }

    var assertionOptions = PasskeyWebAuthn.CreateAssertionOptions(request, options.Value, credential);
    challenges.Create(assertionOptions.Challenge, new PasskeyChallenge(
        PasskeyChallengeKind.Assertion,
        username,
        FormToDictionary(form),
        DateTimeOffset.UtcNow.AddMinutes(5)));

    return Results.Json(assertionOptions.Response);
}).RequireRateLimiting(OAuthRateLimitPolicy);

app.MapPost("/passkeys/login/complete", CompletePasskeyLogin).RequireRateLimiting(OAuthRateLimitPolicy);

app.MapPost("/token", async (
    HttpRequest request,
    AuthorizationCodeStore codes,
    RefreshTokenStore refreshTokens,
    IOptions<OAuthOptions> options) =>
{
    var form = await request.ReadFormAsync();

    return form["grant_type"].ToString() switch
    {
        "authorization_code" => RedeemAuthorizationCode(form, codes, refreshTokens, options.Value),
        "refresh_token" => RedeemRefreshToken(form, refreshTokens, options.Value),
        _ => Results.BadRequest(new { error = "unsupported_grant_type" })
    };
}).RequireRateLimiting(OAuthRateLimitPolicy);

static IResult RedeemAuthorizationCode(
    IFormCollection form,
    AuthorizationCodeStore codes,
    RefreshTokenStore refreshTokens,
    OAuthOptions options)
{
    var now = DateTimeOffset.UtcNow;

    if (!codes.TryRedeem(form["code"].ToString(), out var code) ||
        code.ExpiresAt <= now ||
        !string.Equals(code.ClientId, form["client_id"].ToString(), StringComparison.Ordinal) ||
        !string.Equals(code.RedirectUri, form["redirect_uri"].ToString(), StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "invalid_grant" });
    }

    if (!ValidateCodeVerifier(code, form["code_verifier"].ToString()))
    {
        return Results.BadRequest(new { error = "invalid_grant", error_description = "Invalid PKCE code verifier." });
    }

    var refreshToken = refreshTokens.Create(new RefreshToken(
        ClientId: code.ClientId,
        Scope: code.Scope,
        Resource: code.Resource,
        Subject: code.Subject,
        ExpiresAt: now.AddSeconds(options.RefreshTokenLifetimeSeconds)));

    return IssueTokenResponse(options, code.Subject, code.Scope, code.Resource, refreshToken, now);
}

static IResult RedeemRefreshToken(
    IFormCollection form,
    RefreshTokenStore refreshTokens,
    OAuthOptions options)
{
    var now = DateTimeOffset.UtcNow;
    var refreshTokenValue = form["refresh_token"].ToString();
    if (string.IsNullOrWhiteSpace(refreshTokenValue) ||
        !refreshTokens.TryGet(refreshTokenValue, out var token))
    {
        return Results.BadRequest(new { error = "invalid_grant" });
    }

    if (token.ExpiresAt <= now)
    {
        refreshTokens.Revoke(refreshTokenValue);
        return Results.BadRequest(new { error = "invalid_grant" });
    }

    if (!string.Equals(token.ClientId, form["client_id"].ToString(), StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "invalid_grant" });
    }

    var requestedScope = EmptyToNull(form["scope"].ToString());
    var scope = requestedScope ?? token.Scope;
    if (!IsScopeSubset(scope, token.Scope))
    {
        return Results.BadRequest(new { error = "invalid_scope" });
    }

    var requestedResource = EmptyToNull(form["resource"].ToString());
    if (requestedResource is not null &&
        !string.Equals(requestedResource, token.Resource, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "invalid_target" });
    }

    if (!refreshTokens.TryRedeem(refreshTokenValue, token))
    {
        return Results.BadRequest(new { error = "invalid_grant" });
    }

    var rotatedRefreshToken = refreshTokens.Create(token with
    {
        Scope = scope,
        ExpiresAt = now.AddSeconds(options.RefreshTokenLifetimeSeconds)
    });

    return IssueTokenResponse(options, token.Subject, scope, token.Resource, rotatedRefreshToken, now);
}

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
        grant_types = new[] { "authorization_code", "refresh_token" },
        response_types = new[] { "code" },
        token_endpoint_auth_method = "none"
    }, statusCode: StatusCodes.Status201Created);
}).RequireRateLimiting(OAuthRateLimitPolicy);

app.Run();

static async Task<IResult> CompletePasskeyLogin(
    HttpRequest request,
    AuthorizationCodeStore codes,
    PasskeyChallengeStore challenges,
    PasskeyCredentialStore credentials,
    IOptions<OAuthOptions> oauthOptions,
    IOptions<PasskeyOptions> passkeyOptions)
{
    if (!passkeyOptions.Value.Enabled)
    {
        return Results.NotFound();
    }

    var result = await JsonSerializer.DeserializeAsync<PasskeyAssertionResult>(request.Body);
    if (result is null ||
        !challenges.TryRedeem(result.Challenge, out var challenge) ||
        challenge.Kind is not PasskeyChallengeKind.Assertion ||
        challenge.ExpiresAt <= DateTimeOffset.UtcNow)
    {
        return Results.BadRequest(new { error = "invalid_request" });
    }

    var credential = credentials.GetByUsername(challenge.Username);
    if (credential is null)
    {
        return Results.BadRequest(new { error = "invalid_passkey", error_description = "No passkey is registered for this user." });
    }

    if (!PasskeyWebAuthn.TryCompleteAssertion(
        request,
        passkeyOptions.Value,
        credential,
        result,
        out var signCount,
        out var assertionError))
    {
        return Results.BadRequest(new { error = "invalid_passkey", error_description = assertionError });
    }

    credentials.UpdateSignCount(credential.Username, signCount);
    if (!TryCreateAuthorizationRedirectUrl(challenge.Parameters, challenge.Username, codes, oauthOptions.Value, out var redirectUri, out var redirectError))
    {
        return redirectError;
    }

    return Results.Json(new { redirect_uri = redirectUri });
}

static string GetRateLimitPartitionKey(HttpContext context)
{
    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

static IResult CreateAuthorizationRedirect(
    IReadOnlyDictionary<string, string> parameters,
    string subject,
    AuthorizationCodeStore codes,
    OAuthOptions options)
{
    return TryCreateAuthorizationRedirectUrl(parameters, subject, codes, options, out var redirectUri, out var error)
        ? Results.Redirect(redirectUri)
        : error;
}

static bool TryCreateAuthorizationRedirectUrl(
    IReadOnlyDictionary<string, string> parameters,
    string subject,
    AuthorizationCodeStore codes,
    OAuthOptions options,
    out string redirectUrl,
    out IResult error)
{
    var redirectUri = parameters.GetValueOrDefault("redirect_uri") ?? string.Empty;
    if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect))
    {
        redirectUrl = string.Empty;
        error = Results.BadRequest(new { error = "invalid_request" });
        return false;
    }

    var code = codes.Create(new AuthorizationCode(
        ClientId: parameters.GetValueOrDefault("client_id") ?? string.Empty,
        RedirectUri: redirectUri,
        CodeChallenge: EmptyToNull(parameters.GetValueOrDefault("code_challenge") ?? string.Empty),
        CodeChallengeMethod: EmptyToNull(parameters.GetValueOrDefault("code_challenge_method") ?? string.Empty) ?? "plain",
        Scope: EmptyToNull(parameters.GetValueOrDefault("scope") ?? string.Empty) ?? "mcp",
        Resource: EmptyToNull(parameters.GetValueOrDefault("resource") ?? string.Empty),
        Subject: subject,
        ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(options.AuthorizationCodeLifetimeSeconds)));

    var query = new Dictionary<string, string?>
    {
        ["code"] = code,
        ["state"] = EmptyToNull(parameters.GetValueOrDefault("state") ?? string.Empty)
    };

    redirectUrl = AppendQuery(redirect, query);
    error = Results.Empty;
    return true;
}

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
    button.secondary { background: #57606a; }
    .error { margin: 0; color: #cf222e; font-size: 14px; }
    .hint { margin: -6px 0 0; color: #57606a; font-size: 13px; }
  </style>
</head>
<body>
  <main>
    <form id="login-form" method="post" action="/authorize">
      <h1>Hello Docker MCP</h1>
      {{errorHtml}}
      {{hiddenInputs}}
      <label>Username <input name="username" autocomplete="username" required /></label>
      <label>Password <input name="password" type="password" autocomplete="current-password" required /></label>
      <button type="submit">Sign in</button>
      <button type="button" class="secondary" id="passkey-login">Sign in with passkey</button>
      <button type="button" class="secondary" id="passkey-register">Register passkey</button>
      <p class="hint">Registering a passkey requires the username and password once.</p>
    </form>
  </main>
  <script>
    const form = document.getElementById('login-form');
    const encoder = new TextEncoder();

    function base64UrlToBytes(value) {
      const padded = value.replace(/-/g, '+').replace(/_/g, '/').padEnd(value.length + (4 - value.length % 4) % 4, '=');
      return Uint8Array.from(atob(padded), c => c.charCodeAt(0));
    }

    function bytesToBase64Url(value) {
      let binary = '';
      for (const byte of new Uint8Array(value)) {
        binary += String.fromCharCode(byte);
      }
      return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
    }

    function formData() {
      return new FormData(form);
    }

    async function postForm(url) {
      const response = await fetch(url, { method: 'POST', body: formData() });
      if (!response.ok) {
        throw new Error((await response.json()).error_description ?? 'Passkey request failed.');
      }
      return response.json();
    }

    async function postJson(url, body) {
      const response = await fetch(url, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(body)
      });
      if (!response.ok) {
        throw new Error((await response.json()).error_description ?? 'Passkey request failed.');
      }
      return response.json();
    }

    document.getElementById('passkey-register').addEventListener('click', async () => {
      try {
        const options = await postForm('/passkeys/register/options');
        options.publicKey.challenge = base64UrlToBytes(options.publicKey.challenge);
        options.publicKey.user.id = encoder.encode(options.publicKey.user.id);
        if (options.publicKey.excludeCredentials) {
          options.publicKey.excludeCredentials = options.publicKey.excludeCredentials.map(credential => ({
            ...credential,
            id: base64UrlToBytes(credential.id)
          }));
        }

        const credential = await navigator.credentials.create(options);
        await postJson('/passkeys/register/complete', {
          challenge: options.challenge,
          id: credential.id,
          rawId: bytesToBase64Url(credential.rawId),
          type: credential.type,
          response: {
            clientDataJSON: bytesToBase64Url(credential.response.clientDataJSON),
            attestationObject: bytesToBase64Url(credential.response.attestationObject)
          }
        });
        alert('Passkey registered.');
      } catch (error) {
        alert(error.message);
      }
    });

    document.getElementById('passkey-login').addEventListener('click', async () => {
      try {
        const options = await postForm('/passkeys/login/options');
        options.publicKey.challenge = base64UrlToBytes(options.publicKey.challenge);
        options.publicKey.allowCredentials = options.publicKey.allowCredentials.map(credential => ({
          ...credential,
          id: base64UrlToBytes(credential.id)
        }));

        const assertion = await navigator.credentials.get(options);
        const result = await postJson('/passkeys/login/complete', {
          challenge: options.challenge,
          id: assertion.id,
          rawId: bytesToBase64Url(assertion.rawId),
          type: assertion.type,
          response: {
            clientDataJSON: bytesToBase64Url(assertion.response.clientDataJSON),
            authenticatorData: bytesToBase64Url(assertion.response.authenticatorData),
            signature: bytesToBase64Url(assertion.response.signature)
          }
        });
        window.location.assign(result.redirect_uri);
      } catch (error) {
        alert(error.message);
      }
    });
  </script>
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

static bool IsScopeSubset(string requestedScope, string originalScope)
{
    var originalScopes = originalScope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    return requestedScope
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .All(originalScopes.Contains);
}

static IResult IssueTokenResponse(
    OAuthOptions options,
    string subject,
    string scope,
    string? resource,
    string refreshToken,
    DateTimeOffset now)
{
    var expiresIn = options.AccessTokenLifetimeSeconds;
    var accessToken = JwtAccessToken.Create(
        options.Issuer,
        options.SigningKey,
        subject,
        scope,
        resource,
        now.AddSeconds(expiresIn),
        now);

    return Results.Json(new
    {
        access_token = accessToken,
        token_type = "Bearer",
        expires_in = expiresIn,
        refresh_token = refreshToken,
        scope
    });
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

    public int RefreshTokenLifetimeSeconds { get; set; } = 2592000;

    public List<AccountOptions> Accounts { get; set; } = new();
}

public sealed class AccountOptions
{
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}

public sealed class AuthRateLimitOptions
{
    public int PermitLimit { get; set; } = 60;

    public int WindowSeconds { get; set; } = 60;

    public int QueueLimit { get; set; }
}

public sealed class PasskeyOptions
{
    public bool Enabled { get; set; } = true;

    public string RelyingPartyName { get; set; } = "Hello Docker MCP";

    public string? RelyingPartyId { get; set; }

    public string CredentialStorePath { get; set; } = "data/passkeys.json";

    public bool RequireUserVerification { get; set; }
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

public sealed record RefreshToken(
    string ClientId,
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

public sealed class RefreshTokenStore
{
    private readonly ConcurrentDictionary<string, RefreshToken> _tokens = new(StringComparer.Ordinal);

    public string Create(RefreshToken token)
    {
        var value = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
        _tokens[value] = token;
        return value;
    }

    public bool TryGet(string refreshToken, out RefreshToken token)
    {
        return _tokens.TryGetValue(refreshToken, out token!);
    }

    public bool TryRedeem(string refreshToken, RefreshToken token)
    {
        return ((ICollection<KeyValuePair<string, RefreshToken>>)_tokens).Remove(new KeyValuePair<string, RefreshToken>(refreshToken, token));
    }

    public void Revoke(string refreshToken)
    {
        _tokens.TryRemove(refreshToken, out _);
    }
}

public sealed record PasskeyCredential(
    string Username,
    string CredentialId,
    string PublicKeyX,
    string PublicKeyY,
    uint SignCount,
    DateTimeOffset CreatedAt);

public enum PasskeyChallengeKind
{
    Registration,
    Assertion
}

public sealed record PasskeyChallenge(
    PasskeyChallengeKind Kind,
    string Username,
    IReadOnlyDictionary<string, string> Parameters,
    DateTimeOffset ExpiresAt);

public sealed class PasskeyChallengeStore
{
    private readonly ConcurrentDictionary<string, PasskeyChallenge> _challenges = new(StringComparer.Ordinal);

    public void Create(string challenge, PasskeyChallenge value)
    {
        _challenges[challenge] = value;
    }

    public bool TryRedeem(string challenge, out PasskeyChallenge value)
    {
        return _challenges.TryRemove(challenge, out value!);
    }
}

public sealed class PasskeyCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private List<PasskeyCredential> _credentials;

    public PasskeyCredentialStore(IOptions<PasskeyOptions> options)
    {
        _path = Path.GetFullPath(options.Value.CredentialStorePath);
        _credentials = Load();
    }

    public PasskeyCredential? GetByUsername(string username)
    {
        lock (_gate)
        {
            return _credentials.FirstOrDefault(credential => string.Equals(credential.Username, username, StringComparison.Ordinal));
        }
    }

    public void Save(PasskeyCredential credential)
    {
        lock (_gate)
        {
            _credentials.RemoveAll(item => string.Equals(item.Username, credential.Username, StringComparison.Ordinal));
            _credentials.Add(credential);
            Persist();
        }
    }

    public void UpdateSignCount(string username, uint signCount)
    {
        lock (_gate)
        {
            var index = _credentials.FindIndex(item => string.Equals(item.Username, username, StringComparison.Ordinal));
            if (index >= 0)
            {
                _credentials[index] = _credentials[index] with { SignCount = signCount };
                Persist();
            }
        }
    }

    private List<PasskeyCredential> Load()
    {
        if (!File.Exists(_path))
        {
            return new List<PasskeyCredential>();
        }

        using var stream = File.OpenRead(_path);
        return JsonSerializer.Deserialize<List<PasskeyCredential>>(stream) ?? new List<PasskeyCredential>();
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        var tempPath = $"{_path}.tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, _credentials, JsonOptions);
        }

        File.Move(tempPath, _path, overwrite: true);
    }
}

public sealed record PasskeyRegistrationResult(
    [property: JsonPropertyName("challenge")]
    string Challenge,
    [property: JsonPropertyName("id")]
    string Id,
    [property: JsonPropertyName("rawId")]
    string RawId,
    [property: JsonPropertyName("type")]
    string Type,
    [property: JsonPropertyName("response")]
    PasskeyRegistrationResponse Response);

public sealed record PasskeyRegistrationResponse(
    [property: JsonPropertyName("clientDataJSON")]
    string ClientDataJSON,
    [property: JsonPropertyName("attestationObject")]
    string AttestationObject);

public sealed record PasskeyAssertionResult(
    [property: JsonPropertyName("challenge")]
    string Challenge,
    [property: JsonPropertyName("id")]
    string Id,
    [property: JsonPropertyName("rawId")]
    string RawId,
    [property: JsonPropertyName("type")]
    string Type,
    [property: JsonPropertyName("response")]
    PasskeyAssertionResponse Response);

public sealed record PasskeyAssertionResponse(
    [property: JsonPropertyName("clientDataJSON")]
    string ClientDataJSON,
    [property: JsonPropertyName("authenticatorData")]
    string AuthenticatorData,
    [property: JsonPropertyName("signature")]
    string Signature);

public static class PasskeyWebAuthn
{
    public static (string Challenge, object Response) CreateRegistrationOptions(
        HttpRequest request,
        PasskeyOptions options,
        string username,
        string? existingCredentialId)
    {
        var challenge = NewChallenge();
        var excludeCredentials = existingCredentialId is null
            ? Array.Empty<object>()
            : new object[] { new { type = "public-key", id = existingCredentialId } };

        return (challenge, new
        {
            challenge,
            publicKey = new
            {
                challenge,
                rp = new { name = options.RelyingPartyName, id = GetRelyingPartyId(request, options) },
                user = new { id = username, name = username, displayName = username },
                pubKeyCredParams = new[] { new { type = "public-key", alg = -7 } },
                timeout = 60000,
                attestation = "none",
                excludeCredentials,
                authenticatorSelection = new
                {
                    residentKey = "preferred",
                    userVerification = options.RequireUserVerification ? "required" : "preferred"
                }
            }
        });
    }

    public static (string Challenge, object Response) CreateAssertionOptions(
        HttpRequest request,
        PasskeyOptions options,
        PasskeyCredential credential)
    {
        var challenge = NewChallenge();
        return (challenge, new
        {
            challenge,
            publicKey = new
            {
                challenge,
                rpId = GetRelyingPartyId(request, options),
                timeout = 60000,
                userVerification = options.RequireUserVerification ? "required" : "preferred",
                allowCredentials = new[] { new { type = "public-key", id = credential.CredentialId } }
            }
        });
    }

    public static bool TryCompleteRegistration(
        HttpRequest request,
        PasskeyOptions options,
        string username,
        PasskeyRegistrationResult result,
        out PasskeyCredential credential,
        out string error)
    {
        credential = default!;
        if (!IsPublicKeyResult(result.Type, result.Id, result.RawId, out error))
        {
            return false;
        }

        var clientData = Base64UrlDecode(result.Response.ClientDataJSON);
        if (!ValidateClientData(clientData, "webauthn.create", result.Challenge, GetOrigin(request), out error))
        {
            return false;
        }

        var attestationObject = Base64UrlDecode(result.Response.AttestationObject);
        if (!MinimalCbor.TryReadAttestationAuthData(attestationObject, out var authData, out error) ||
            !TryParseAttestedCredentialData(authData, GetRelyingPartyId(request, options), options.RequireUserVerification, out var credentialId, out var publicKeyX, out var publicKeyY, out var signCount, out error))
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(Base64UrlDecode(result.RawId), credentialId))
        {
            error = "Credential id mismatch.";
            return false;
        }

        credential = new PasskeyCredential(
            username,
            Base64UrlEncode(credentialId),
            Base64UrlEncode(publicKeyX),
            Base64UrlEncode(publicKeyY),
            signCount,
            DateTimeOffset.UtcNow);
        return true;
    }

    public static bool TryCompleteAssertion(
        HttpRequest request,
        PasskeyOptions options,
        PasskeyCredential credential,
        PasskeyAssertionResult result,
        out uint signCount,
        out string error)
    {
        signCount = 0;
        if (!IsPublicKeyResult(result.Type, result.Id, result.RawId, out error) ||
            !string.Equals(result.RawId, credential.CredentialId, StringComparison.Ordinal))
        {
            error = "Credential id mismatch.";
            return false;
        }

        var clientData = Base64UrlDecode(result.Response.ClientDataJSON);
        if (!ValidateClientData(clientData, "webauthn.get", result.Challenge, GetOrigin(request), out error))
        {
            return false;
        }

        var authData = Base64UrlDecode(result.Response.AuthenticatorData);
        if (!ValidateAuthenticatorData(authData, GetRelyingPartyId(request, options), options.RequireUserVerification, out signCount, out error))
        {
            return false;
        }

        if (credential.SignCount > 0 && signCount > 0 && signCount <= credential.SignCount)
        {
            error = "Authenticator sign count did not increase.";
            return false;
        }

        var signedData = authData.Concat(SHA256.HashData(clientData)).ToArray();
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = Base64UrlDecode(credential.PublicKeyX),
                Y = Base64UrlDecode(credential.PublicKeyY)
            }
        });

        if (!key.VerifyData(signedData, Base64UrlDecode(result.Response.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
        {
            error = "Invalid passkey signature.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsPublicKeyResult(string type, string id, string rawId, out string error)
    {
        if (!string.Equals(type, "public-key", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(id) ||
            string.IsNullOrWhiteSpace(rawId))
        {
            error = "Invalid WebAuthn credential.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidateClientData(byte[] clientData, string expectedType, string expectedChallenge, string expectedOrigin, out string error)
    {
        using var document = JsonDocument.Parse(clientData);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type) ||
            !string.Equals(type.GetString(), expectedType, StringComparison.Ordinal) ||
            !root.TryGetProperty("challenge", out var challenge) ||
            !string.Equals(challenge.GetString(), expectedChallenge, StringComparison.Ordinal) ||
            !root.TryGetProperty("origin", out var origin) ||
            !string.Equals(origin.GetString(), expectedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            error = "Invalid WebAuthn client data.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParseAttestedCredentialData(
        byte[] authData,
        string relyingPartyId,
        bool requireUserVerification,
        out byte[] credentialId,
        out byte[] publicKeyX,
        out byte[] publicKeyY,
        out uint signCount,
        out string error)
    {
        credentialId = Array.Empty<byte>();
        publicKeyX = Array.Empty<byte>();
        publicKeyY = Array.Empty<byte>();

        if (!ValidateAuthenticatorData(authData, relyingPartyId, requireUserVerification, out signCount, out error))
        {
            return false;
        }

        if ((authData[32] & 0x40) == 0 || authData.Length < 55)
        {
            error = "Authenticator data does not contain attested credential data.";
            return false;
        }

        var credentialLength = BinaryPrimitives.ReadUInt16BigEndian(authData.AsSpan(53, 2));
        var credentialStart = 55;
        var credentialEnd = credentialStart + credentialLength;
        if (credentialEnd > authData.Length)
        {
            error = "Invalid credential id length.";
            return false;
        }

        credentialId = authData[credentialStart..credentialEnd];
        return MinimalCbor.TryReadCoseP256PublicKey(authData[credentialEnd..], out publicKeyX, out publicKeyY, out error);
    }

    private static bool ValidateAuthenticatorData(byte[] authData, string relyingPartyId, bool requireUserVerification, out uint signCount, out string error)
    {
        signCount = 0;
        if (authData.Length < 37)
        {
            error = "Authenticator data is too short.";
            return false;
        }

        var expectedRpHash = SHA256.HashData(Encoding.UTF8.GetBytes(relyingPartyId));
        if (!CryptographicOperations.FixedTimeEquals(authData.AsSpan(0, 32), expectedRpHash))
        {
            error = "Relying party id hash mismatch.";
            return false;
        }

        var flags = authData[32];
        if ((flags & 0x01) == 0)
        {
            error = "User presence is required.";
            return false;
        }

        if (requireUserVerification && (flags & 0x04) == 0)
        {
            error = "User verification is required.";
            return false;
        }

        signCount = BinaryPrimitives.ReadUInt32BigEndian(authData.AsSpan(33, 4));
        error = string.Empty;
        return true;
    }

    private static string GetOrigin(HttpRequest request)
    {
        return $"{request.Scheme}://{request.Host}";
    }

    private static string GetRelyingPartyId(HttpRequest request, PasskeyOptions options)
    {
        return string.IsNullOrWhiteSpace(options.RelyingPartyId)
            ? request.Host.Host
            : options.RelyingPartyId;
    }

    private static string NewChallenge()
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
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

public static class MinimalCbor
{
    public static bool TryReadAttestationAuthData(byte[] cbor, out byte[] authData, out string error)
    {
        authData = Array.Empty<byte>();
        var reader = new Reader(cbor);
        if (!reader.TryReadMapHeader(out var count))
        {
            error = "Invalid attestation object.";
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadTextString(out var key))
            {
                error = "Invalid attestation object key.";
                return false;
            }

            if (key == "authData")
            {
                if (!reader.TryReadByteString(out authData))
                {
                    error = "Missing authenticator data.";
                    return false;
                }
            }
            else if (!reader.TrySkip())
            {
                error = "Invalid attestation object value.";
                return false;
            }
        }

        error = authData.Length == 0 ? "Missing authenticator data." : string.Empty;
        return authData.Length > 0;
    }

    public static bool TryReadCoseP256PublicKey(byte[] cbor, out byte[] x, out byte[] y, out string error)
    {
        x = Array.Empty<byte>();
        y = Array.Empty<byte>();
        int? keyType = null;
        int? algorithm = null;
        int? curve = null;
        var reader = new Reader(cbor);

        if (!reader.TryReadMapHeader(out var count))
        {
            error = "Invalid COSE key.";
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadInt(out var key))
            {
                error = "Invalid COSE key label.";
                return false;
            }

            switch (key)
            {
                case 1:
                    reader.TryReadInt(out var kty);
                    keyType = kty;
                    break;
                case 3:
                    reader.TryReadInt(out var alg);
                    algorithm = alg;
                    break;
                case -1:
                    reader.TryReadInt(out var crv);
                    curve = crv;
                    break;
                case -2:
                    reader.TryReadByteString(out x);
                    break;
                case -3:
                    reader.TryReadByteString(out y);
                    break;
                default:
                    if (!reader.TrySkip())
                    {
                        error = "Invalid COSE key value.";
                        return false;
                    }

                    break;
            }
        }

        if (keyType != 2 || algorithm != -7 || curve != 1 || x.Length != 32 || y.Length != 32)
        {
            error = "Only ES256 P-256 passkeys are supported.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public Reader(byte[] data)
        {
            _data = data;
            _offset = 0;
        }

        public bool TryReadMapHeader(out int count)
        {
            return TryReadTypeAndLength(5, out count);
        }

        public bool TryReadTextString(out string value)
        {
            value = string.Empty;
            if (!TryReadTypeAndLength(3, out var length) || _offset + length > _data.Length)
            {
                return false;
            }

            value = Encoding.UTF8.GetString(_data.Slice(_offset, length));
            _offset += length;
            return true;
        }

        public bool TryReadByteString(out byte[] value)
        {
            value = Array.Empty<byte>();
            if (!TryReadTypeAndLength(2, out var length) || _offset + length > _data.Length)
            {
                return false;
            }

            value = _data.Slice(_offset, length).ToArray();
            _offset += length;
            return true;
        }

        public bool TryReadInt(out int value)
        {
            value = 0;
            if (!TryReadInitial(out var majorType, out var additionalInfo) ||
                !TryReadLength(additionalInfo, out var unsignedValue) ||
                unsignedValue > int.MaxValue)
            {
                return false;
            }

            value = majorType switch
            {
                0 => (int)unsignedValue,
                1 => -1 - (int)unsignedValue,
                _ => 0
            };
            return majorType is 0 or 1;
        }

        public bool TrySkip()
        {
            if (!TryReadInitial(out var majorType, out var additionalInfo))
            {
                return false;
            }

            if (majorType is 0 or 1)
            {
                return TryReadLength(additionalInfo, out _);
            }

            if (majorType is 2 or 3)
            {
                return TryReadLength(additionalInfo, out var length) &&
                    length <= int.MaxValue &&
                    Skip((int)length);
            }

            if (majorType == 4)
            {
                if (!TryReadLength(additionalInfo, out var count) || count > int.MaxValue)
                {
                    return false;
                }

                for (var i = 0; i < (int)count; i++)
                {
                    if (!TrySkip())
                    {
                        return false;
                    }
                }

                return true;
            }

            if (majorType == 5)
            {
                if (!TryReadLength(additionalInfo, out var count) || count > int.MaxValue)
                {
                    return false;
                }

                for (var i = 0; i < (int)count * 2; i++)
                {
                    if (!TrySkip())
                    {
                        return false;
                    }
                }

                return true;
            }

            if (majorType == 7)
            {
                return additionalInfo < 24 || TryReadLength(additionalInfo, out _);
            }

            return false;
        }

        private bool TryReadTypeAndLength(int expectedMajorType, out int length)
        {
            length = 0;
            if (!TryReadInitial(out var majorType, out var additionalInfo) ||
                majorType != expectedMajorType ||
                !TryReadLength(additionalInfo, out var unsignedLength) ||
                unsignedLength > int.MaxValue)
            {
                return false;
            }

            length = (int)unsignedLength;
            return true;
        }

        private bool TryReadInitial(out int majorType, out int additionalInfo)
        {
            majorType = 0;
            additionalInfo = 0;
            if (_offset >= _data.Length)
            {
                return false;
            }

            var initial = _data[_offset++];
            majorType = initial >> 5;
            additionalInfo = initial & 0x1f;
            return additionalInfo != 31;
        }

        private bool TryReadLength(int additionalInfo, out ulong value)
        {
            value = 0;
            if (additionalInfo < 24)
            {
                value = (ulong)additionalInfo;
                return true;
            }

            if (additionalInfo == 24)
            {
                if (_offset >= _data.Length)
                {
                    return false;
                }

                value = _data[_offset++];
                return true;
            }

            if (additionalInfo == 25)
            {
                if (_offset + 2 > _data.Length)
                {
                    return false;
                }

                value = BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(_offset, 2));
                _offset += 2;
                return true;
            }

            if (additionalInfo == 26)
            {
                if (_offset + 4 > _data.Length)
                {
                    return false;
                }

                value = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_offset, 4));
                _offset += 4;
                return true;
            }

            if (additionalInfo == 27)
            {
                if (_offset + 8 > _data.Length)
                {
                    return false;
                }

                value = BinaryPrimitives.ReadUInt64BigEndian(_data.Slice(_offset, 8));
                _offset += 8;
                return true;
            }

            return false;
        }

        private bool Skip(int length)
        {
            if (_offset + length > _data.Length)
            {
                return false;
            }

            _offset += length;
            return true;
        }
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
        DateTimeOffset expiresAt,
        DateTimeOffset issuedAt)
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
            iat = issuedAt.ToUnixTimeSeconds()
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
