using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

public sealed class StorageService
{
    private const string DefaultRootPath = "/storage";
    private readonly string _rootPath;

    public StorageService(IConfiguration configuration)
    {
        _rootPath = Path.GetFullPath(
            Environment.GetEnvironmentVariable("STORAGE_ROOT") ??
            Environment.GetEnvironmentVariable("Storage__RootPath") ??
            configuration["Storage:RootPath"] ??
            DefaultRootPath);
        Directory.CreateDirectory(_rootPath);
        TryChmod777(_rootPath);
    }

    public string RootPath => _rootPath;

    public string ResolveStoragePath(string? path)
    {
        return ResolvePath(path);
    }

    public string ResolveStorageFile(string? path)
    {
        return ResolveRequiredFile(path);
    }

    public object GetInfo()
    {
        var directory = new DirectoryInfo(_rootPath);
        return new
        {
            ok = true,
            rootPath = _rootPath,
            exists = directory.Exists,
            mode = GetUnixMode(_rootPath),
            note = "AI has full permission in this storage directory. This API is limited to the configured storage root."
        };
    }

    public object List(string? path, bool recursive)
    {
        try
        {
            var fullPath = ResolvePath(path);
            if (!Directory.Exists(fullPath))
            {
                return Error("DIRECTORY_NOT_FOUND", $"Directory not found: {NormalizeRelativePath(path)}.");
            }

            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var entries = Directory.EnumerateFileSystemEntries(fullPath, "*", option)
                .Select(entry =>
                {
                    var isDirectory = Directory.Exists(entry);
                    var info = isDirectory
                        ? new DirectoryInfo(entry) as FileSystemInfo
                        : new FileInfo(entry);
                    return new
                    {
                        path = ToStoragePath(entry),
                        name = Path.GetFileName(entry),
                        type = isDirectory ? "directory" : "file",
                        sizeBytes = isDirectory ? null : (long?)new FileInfo(entry).Length,
                        lastWriteTimeUtc = info.LastWriteTimeUtc,
                        mode = GetUnixMode(entry)
                    };
                })
                .OrderBy(entry => entry.path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new
            {
                ok = true,
                path = ToStoragePath(fullPath),
                recursive,
                entries
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> ReadTextAsync(string? path, int maxBytes)
    {
        try
        {
            var fullPath = ResolveRequiredFile(path);
            maxBytes = Math.Clamp(maxBytes, 1, 1_048_576);
            var bytes = await WithPermissionRetryAsync(fullPath, () => ReadBytesLimitedAsync(fullPath, maxBytes));
            return new
            {
                ok = true,
                path = ToStoragePath(fullPath),
                encoding = "utf-8",
                truncated = new FileInfo(fullPath).Length > bytes.Length,
                content = Encoding.UTF8.GetString(bytes)
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> WriteTextAsync(string? path, string? content, bool overwrite)
    {
        try
        {
            var fullPath = ResolvePath(path);
            EnsureParentDirectory(fullPath);
            if (!overwrite && File.Exists(fullPath))
            {
                return Error("FILE_ALREADY_EXISTS", $"File already exists: {ToStoragePath(fullPath)}.");
            }

            await WithPermissionRetryAsync(fullPath, async () =>
            {
                await File.WriteAllTextAsync(fullPath, content ?? string.Empty, new UTF8Encoding(false));
                return true;
            });

            TryChmod777(fullPath);
            return new
            {
                ok = true,
                path = ToStoragePath(fullPath),
                createdOrUpdated = true,
                sizeBytes = new FileInfo(fullPath).Length,
                mode = GetUnixMode(fullPath)
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> WriteBase64Async(string? path, string? base64Content, bool overwrite)
    {
        try
        {
            var fullPath = ResolvePath(path);
            EnsureParentDirectory(fullPath);
            if (!overwrite && File.Exists(fullPath))
            {
                return Error("FILE_ALREADY_EXISTS", $"File already exists: {ToStoragePath(fullPath)}.");
            }

            var bytes = Convert.FromBase64String(base64Content ?? string.Empty);
            await WithPermissionRetryAsync(fullPath, async () =>
            {
                await File.WriteAllBytesAsync(fullPath, bytes);
                return true;
            });

            TryChmod777(fullPath);
            return new
            {
                ok = true,
                path = ToStoragePath(fullPath),
                createdOrUpdated = true,
                sizeBytes = bytes.Length,
                mode = GetUnixMode(fullPath)
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public object CreateDirectory(string? path)
    {
        try
        {
            var fullPath = ResolvePath(path);
            Directory.CreateDirectory(fullPath);
            TryChmod777(fullPath);
            return new
            {
                ok = true,
                path = ToStoragePath(fullPath),
                created = true,
                mode = GetUnixMode(fullPath)
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public object Move(string? sourcePath, string? destinationPath, bool overwrite)
    {
        try
        {
            var source = ResolvePath(sourcePath);
            var destination = ResolvePath(destinationPath);
            EnsureParentDirectory(destination);
            return WithPermissionRetry(source, () =>
            {
                if (Directory.Exists(source))
                {
                    if (Directory.Exists(destination) || File.Exists(destination))
                    {
                        if (!overwrite)
                        {
                            return Error("DESTINATION_ALREADY_EXISTS", $"Destination already exists: {ToStoragePath(destination)}.");
                        }

                        DeleteExisting(destination);
                    }

                    Directory.Move(source, destination);
                }
                else if (File.Exists(source))
                {
                    File.Move(source, destination, overwrite);
                }
                else
                {
                    return Error("SOURCE_NOT_FOUND", $"Source not found: {ToStoragePath(source)}.");
                }

                TryChmod777(destination);
                return new
                {
                    ok = true,
                    source = ToStoragePath(source),
                    destination = ToStoragePath(destination),
                    moved = true
                };
            });
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public object Copy(string? sourcePath, string? destinationPath, bool overwrite)
    {
        try
        {
            var source = ResolvePath(sourcePath);
            var destination = ResolvePath(destinationPath);
            EnsureParentDirectory(destination);
            return WithPermissionRetry(source, () =>
            {
                if (Directory.Exists(source))
                {
                    CopyDirectory(source, destination, overwrite);
                }
                else if (File.Exists(source))
                {
                    File.Copy(source, destination, overwrite);
                }
                else
                {
                    return Error("SOURCE_NOT_FOUND", $"Source not found: {ToStoragePath(source)}.");
                }

                TryChmod777(destination);
                return new
                {
                    ok = true,
                    source = ToStoragePath(source),
                    destination = ToStoragePath(destination),
                    copied = true
                };
            });
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public object Delete(string? path, bool recursive)
    {
        try
        {
            var fullPath = ResolvePath(path);
            return WithPermissionRetry(fullPath, () =>
            {
                if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, recursive);
                }
                else if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
                else
                {
                    return Error("PATH_NOT_FOUND", $"Path not found: {ToStoragePath(fullPath)}.");
                }

                return new
                {
                    ok = true,
                    path = ToStoragePath(fullPath),
                    deleted = true
                };
            });
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> BatchAsync(JsonElement? operations)
    {
        try
        {
            if (operations is null || operations.Value.ValueKind != JsonValueKind.Array)
            {
                return Error("MISSING_REQUIRED_ARGUMENT", "operations must be an array.");
            }

            var results = new List<object>();
            foreach (var operation in operations.Value.EnumerateArray())
            {
                var action = operation.TryGetProperty("action", out var actionElement)
                    ? actionElement.GetString()
                    : null;
                var path = operation.TryGetProperty("path", out var pathElement)
                    ? pathElement.GetString()
                    : null;

                results.Add(action?.Trim().ToLowerInvariant() switch
                {
                    "list" => List(path, GetBool(operation, "recursive", false)),
                    "readText" or "read_text" => await ReadTextAsync(path, GetInt(operation, "maxBytes", 262144)),
                    "writeText" or "write_text" => await WriteTextAsync(path, GetString(operation, "content"), GetBool(operation, "overwrite", true)),
                    "writeBase64" or "write_base64" => await WriteBase64Async(path, GetString(operation, "base64Content"), GetBool(operation, "overwrite", true)),
                    "createDirectory" or "create_directory" => CreateDirectory(path),
                    "move" => Move(path, GetString(operation, "destinationPath"), GetBool(operation, "overwrite", true)),
                    "copy" => Copy(path, GetString(operation, "destinationPath"), GetBool(operation, "overwrite", true)),
                    "delete" => Delete(path, GetBool(operation, "recursive", false)),
                    _ => Error("INVALID_BATCH_ACTION", $"Unsupported storage batch action: {action}.")
                });
            }

            return new
            {
                ok = true,
                count = results.Count,
                results
            };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    private string ResolveRequiredFile(string? path)
    {
        var fullPath = ResolvePath(path);
        if (!File.Exists(fullPath))
        {
            throw new StorageToolException("FILE_NOT_FOUND", $"File not found: {ToStoragePath(fullPath)}.");
        }

        return fullPath;
    }

    private string ResolvePath(string? path)
    {
        var relativePath = NormalizeRelativePath(path);
        var combined = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
        var rootWithSeparator = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        if (!string.Equals(combined, _rootPath, StringComparison.Ordinal) &&
            !combined.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new StorageToolException("PATH_OUTSIDE_STORAGE", "Path must stay inside the storage directory.");
        }

        return combined;
    }

    private static string NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Trim() == "/" || path.Trim() == ".")
        {
            return string.Empty;
        }

        return path.Trim().Replace('\\', '/').TrimStart('/');
    }

    private string ToStoragePath(string fullPath)
    {
        var relative = Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');
        return relative == "." ? "/" : "/" + relative;
    }

    private static async Task<byte[]> ReadBytesLimitedAsync(string path, int maxBytes)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        var bytes = new byte[Math.Min(8192, maxBytes)];
        while (buffer.Length < maxBytes)
        {
            var remaining = maxBytes - (int)buffer.Length;
            var read = await stream.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, remaining)));
            if (read == 0)
            {
                break;
            }

            buffer.Write(bytes, 0, read);
        }

        return buffer.ToArray();
    }

    private void EnsureParentDirectory(string fullPath)
    {
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
            TryChmod777(parent);
        }
    }

    private T WithPermissionRetry<T>(string path, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (UnauthorizedAccessException)
        {
            EnsurePermissionsFor(path);
            return action();
        }
    }

    private async Task<T> WithPermissionRetryAsync<T>(string path, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (UnauthorizedAccessException)
        {
            EnsurePermissionsFor(path);
            return await action();
        }
    }

    private void EnsurePermissionsFor(string path)
    {
        var current = path;
        while (!string.IsNullOrWhiteSpace(current) &&
            Path.GetFullPath(current).StartsWith(_rootPath, StringComparison.Ordinal))
        {
            TryChmod777(current);
            if (string.Equals(Path.GetFullPath(current), _rootPath, StringComparison.Ordinal))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static void TryChmod777(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            }
        }
        catch
        {
            // Permission repair is best-effort; the original operation will report failure if it still cannot proceed.
        }
    }

    private static string? GetUnixMode(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return null;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            return Convert.ToString((int)mode & 0x1ff, 8).PadLeft(3, '0');
        }
        catch
        {
            return null;
        }
    }

    private static void CopyDirectory(string source, string destination, bool overwrite)
    {
        if (Directory.Exists(destination) && !overwrite)
        {
            throw new StorageToolException("DESTINATION_ALREADY_EXISTS", $"Destination already exists: {destination}.");
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), overwrite);
        }
    }

    private static void DeleteExisting(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string? GetString(JsonElement operation, string name)
    {
        return operation.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool GetBool(JsonElement operation, string name, bool defaultValue)
    {
        return operation.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;
    }

    private static int GetInt(JsonElement operation, string name, int defaultValue)
    {
        return operation.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : defaultValue;
    }

    private static object ToErrorResult(Exception ex)
    {
        if (ex is StorageToolException toolException)
        {
            return Error(toolException.ErrorCode, toolException.Message);
        }

        if (ex is FormatException)
        {
            return Error("INVALID_BASE64", "base64Content is not valid Base64.");
        }

        return Error("STORAGE_OPERATION_FAILED", ex.Message);
    }

    private static object Error(string errorCode, string message)
    {
        return new
        {
            ok = false,
            errorCode,
            message,
            hint = "Use a relative path inside the configured storage directory."
        };
    }
}

public sealed class StorageToolException : Exception
{
    public StorageToolException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
