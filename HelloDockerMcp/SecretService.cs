using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

public sealed class SecretService
{
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int KeyBytes = 32;
    private const int Pbkdf2Iterations = 210_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SecretOptions _options;
    private readonly object _lock = new();

    public SecretService(IOptions<SecretOptions> options)
    {
        _options = options.Value;
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath) ?? ".");
    }

    public string StorePath => Path.GetFullPath(_options.StorePath);

    public bool ValidateAdmin(string? username, string? password)
    {
        return FixedEquals(username ?? string.Empty, _options.AdminUsername) &&
            FixedEquals(password ?? string.Empty, _options.AdminPassword);
    }

    public IReadOnlyList<SecretMetadata> List()
    {
        lock (_lock)
        {
            var store = LoadStore();
            return store.Secrets
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new SecretMetadata(
                    pair.Key,
                    pair.Value.CreatedAt,
                    pair.Value.UpdatedAt))
                .ToList();
        }
    }

    public void Set(string? key, string? value)
    {
        var normalizedKey = NormalizeKey(key);
        if (value is null)
        {
            throw new SecretToolException("MISSING_REQUIRED_ARGUMENT", "value is required.");
        }

        lock (_lock)
        {
            var store = LoadStore();
            var now = DateTimeOffset.UtcNow;
            var encrypted = Encrypt(value, store.Salt);
            var createdAt = store.Secrets.TryGetValue(normalizedKey, out var existing)
                ? existing.CreatedAt
                : now;
            store.Secrets[normalizedKey] = new SecretEntry
            {
                CreatedAt = createdAt,
                UpdatedAt = now,
                Nonce = encrypted.Nonce,
                Ciphertext = encrypted.Ciphertext
            };
            SaveStore(store);
        }
    }

    public bool Delete(string? key)
    {
        var normalizedKey = NormalizeKey(key);
        lock (_lock)
        {
            var store = LoadStore();
            var deleted = store.Secrets.Remove(normalizedKey);
            SaveStore(store);
            return deleted;
        }
    }

    public string GetValue(string? key)
    {
        var normalizedKey = NormalizeKey(key);
        lock (_lock)
        {
            var store = LoadStore();
            if (!store.Secrets.TryGetValue(normalizedKey, out var entry))
            {
                throw new SecretToolException("SECRET_NOT_FOUND", $"Secret not found: {normalizedKey}.");
            }

            try
            {
                return Decrypt(entry, store.Salt);
            }
            catch (CryptographicException ex)
            {
                throw new SecretToolException("SECRET_DECRYPT_FAILED", $"Could not decrypt secret: {normalizedKey}. {ex.Message}");
            }
        }
    }

    public IResult RenderPage(HttpContext context, string? message = null, string? error = null)
    {
        if (!IsLoggedIn(context))
        {
            return Results.Content(RenderLoginPage(error), "text/html; charset=utf-8");
        }

        return Results.Content(RenderSecretPage(List(), message, error), "text/html; charset=utf-8");
    }

    public IResult Login(HttpContext context, IFormCollection form)
    {
        if (!ValidateAdmin(form["username"].ToString(), form["password"].ToString()))
        {
            return Results.Content(RenderLoginPage("Invalid username or password."), "text/html; charset=utf-8");
        }

        context.Response.Cookies.Append(
            "secret_admin",
            CreateSessionToken(),
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                MaxAge = TimeSpan.FromHours(8)
            });
        return Results.Redirect("/secrets");
    }

    public IResult Logout(HttpContext context)
    {
        context.Response.Cookies.Delete("secret_admin");
        return Results.Redirect("/secrets");
    }

    public IResult SetFromForm(HttpContext context, IFormCollection form)
    {
        if (!IsLoggedIn(context))
        {
            return Results.Redirect("/secrets");
        }

        try
        {
            Set(form["key"].ToString(), form["value"].ToString());
            return RenderPage(context, "Secret saved.");
        }
        catch (Exception ex)
        {
            return RenderPage(context, error: ex.Message);
        }
    }

    public IResult DeleteFromForm(HttpContext context, IFormCollection form)
    {
        if (!IsLoggedIn(context))
        {
            return Results.Redirect("/secrets");
        }

        try
        {
            var deleted = Delete(form["key"].ToString());
            return RenderPage(context, deleted ? "Secret deleted." : "Secret key was not found.");
        }
        catch (Exception ex)
        {
            return RenderPage(context, error: ex.Message);
        }
    }

    public object ListForMcp()
    {
        try
        {
            var secrets = List();
            return new
            {
                ok = true,
                count = secrets.Count,
                secrets
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    private bool IsLoggedIn(HttpContext context)
    {
        return context.Request.Cookies.TryGetValue("secret_admin", out var token) &&
            FixedEquals(token ?? string.Empty, CreateSessionToken());
    }

    private string CreateSessionToken()
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.AdminPassword));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"secret-admin:{_options.AdminUsername}")));
    }

    private SecretStoreDocument LoadStore()
    {
        if (!File.Exists(StorePath))
        {
            return new SecretStoreDocument
            {
                Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltBytes)),
                Secrets = new Dictionary<string, SecretEntry>(StringComparer.Ordinal)
            };
        }

        try
        {
            using var stream = File.OpenRead(StorePath);
            var store = JsonSerializer.Deserialize<SecretStoreDocument>(stream, JsonOptions);
            if (store is null || string.IsNullOrWhiteSpace(store.Salt))
            {
                throw new SecretToolException("SECRET_STORE_UNAVAILABLE", "Secret store file is invalid.");
            }

            store.Secrets ??= new Dictionary<string, SecretEntry>(StringComparer.Ordinal);
            return store;
        }
        catch (JsonException ex)
        {
            throw new SecretToolException("SECRET_STORE_UNAVAILABLE", $"Secret store file is invalid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new SecretToolException("SECRET_STORE_UNAVAILABLE", $"Could not read secret store: {ex.Message}");
        }
    }

    private void SaveStore(SecretStoreDocument store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath) ?? ".");
        var tempPath = StorePath + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, store, JsonOptions);
        }

        File.Move(tempPath, StorePath, overwrite: true);
        TrySetUserOnlyReadWrite(StorePath);
    }

    private EncryptedSecret Encrypt(string value, string salt)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(DeriveKey(salt), 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return new EncryptedSecret(
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext.Concat(tag).ToArray()));
    }

    private string Decrypt(SecretEntry entry, string salt)
    {
        var nonce = Convert.FromBase64String(entry.Nonce);
        var combined = Convert.FromBase64String(entry.Ciphertext);
        if (combined.Length < 16)
        {
            throw new CryptographicException("Ciphertext is too short.");
        }

        var ciphertext = combined[..^16];
        var tag = combined[^16..];
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(DeriveKey(salt), 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private byte[] DeriveKey(string salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            _options.AdminPassword,
            Convert.FromBase64String(salt),
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
    }

    private static string NormalizeKey(string? key)
    {
        var normalized = key?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new SecretToolException("MISSING_REQUIRED_ARGUMENT", "key is required.");
        }

        if (normalized.Length > 128 || normalized.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/')))
        {
            throw new SecretToolException("INVALID_SECRET_KEY", "key may contain only letters, digits, dash, underscore, dot, colon, and slash, up to 128 characters.");
        }

        return normalized;
    }

    private static bool FixedEquals(string left, string right)
    {
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(left)),
            SHA256.HashData(Encoding.UTF8.GetBytes(right)));
    }

    private static void TrySetUserOnlyReadWrite(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
        }
    }

    private static string HtmlEncode(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static string RenderLoginPage(string? error)
    {
        return $$"""
<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>Secret Admin</title></head>
<body>
<h1>Secret Admin</h1>
{{RenderNotice(null, error)}}
<form method="post" action="/secrets/login">
  <label>Username <input name="username" autocomplete="username" required></label><br>
  <label>Password <input name="password" type="password" autocomplete="current-password" required></label><br>
  <button type="submit">Login</button>
</form>
</body>
</html>
""";
    }

    private static string RenderSecretPage(IReadOnlyList<SecretMetadata> secrets, string? message, string? error)
    {
        var rows = string.Join(Environment.NewLine, secrets.Select(secret =>
            $"<tr><td>{HtmlEncode(secret.Key)}</td><td>{HtmlEncode(secret.CreatedAt.ToString("u"))}</td><td>{HtmlEncode(secret.UpdatedAt.ToString("u"))}</td><td><form method=\"post\" action=\"/secrets/delete\"><input type=\"hidden\" name=\"key\" value=\"{HtmlEncode(secret.Key)}\"><button type=\"submit\">Delete</button></form></td></tr>"));
        return $$"""
<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>Secret Admin</title></head>
<body>
<h1>Secret Admin</h1>
{{RenderNotice(message, error)}}
<form method="post" action="/secrets/logout"><button type="submit">Logout</button></form>
<h2>Add or update secret</h2>
<form method="post" action="/secrets/set">
  <label>Key <input name="key" required></label><br>
  <label>Value <input name="value" type="password" autocomplete="off" required></label><br>
  <button type="submit">Save</button>
</form>
<h2>Secret keys</h2>
<table border="1" cellpadding="4" cellspacing="0">
<thead><tr><th>Key</th><th>Created UTC</th><th>Updated UTC</th><th>Action</th></tr></thead>
<tbody>
{{rows}}
</tbody>
</table>
</body>
</html>
""";
    }

    private static string RenderNotice(string? message, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return $"<p style=\"color:#b00020\">{HtmlEncode(error)}</p>";
        }

        return string.IsNullOrWhiteSpace(message) ? string.Empty : $"<p style=\"color:#006400\">{HtmlEncode(message)}</p>";
    }

    private static object ToErrorResult(Exception ex)
    {
        if (ex is SecretToolException secretException)
        {
            return new { ok = false, errorCode = secretException.ErrorCode, message = secretException.Message };
        }

        return new { ok = false, errorCode = "SECRET_STORE_UNAVAILABLE", message = ex.Message };
    }

    private sealed record EncryptedSecret(string Nonce, string Ciphertext);
}

public sealed class SecretOptions
{
    public string AdminUsername { get; set; } = "admin";

    public string AdminPassword { get; set; } = "change-this-secret-admin-password";

    public string StorePath { get; set; } = "/secrets/secrets.json";
}

public sealed record SecretMetadata(string Key, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed class SecretStoreDocument
{
    public int Version { get; set; } = 1;

    public string Salt { get; set; } = string.Empty;

    public Dictionary<string, SecretEntry> Secrets { get; set; } = new(StringComparer.Ordinal);
}

public sealed class SecretEntry
{
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string Nonce { get; set; } = string.Empty;

    public string Ciphertext { get; set; } = string.Empty;
}

public sealed class SecretToolException : Exception
{
    public SecretToolException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
