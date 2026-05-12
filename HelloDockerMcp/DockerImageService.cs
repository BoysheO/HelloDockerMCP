using Docker.DotNet;
using Docker.DotNet.Models;
using System.Text;

public sealed class DockerImageService
{
    private readonly DockerClient _client;
    private readonly DockerGuard _guard;
    private readonly StorageService _storage;

    public DockerImageService(DockerGuard guard, StorageService storage)
    {
        _guard = guard;
        _storage = storage;

        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        _client = string.IsNullOrWhiteSpace(dockerHost)
            ? new DockerClientConfiguration().CreateClient()
            : new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();
    }

    public async Task<IReadOnlyList<object>> ListImagesAsync()
    {
        var images = await _client.Images.ListImagesAsync(new ImagesListParameters { All = true });
        return images.Select(image => new
        {
            ok = true,
            id = ShortId(image.ID),
            repoTags = image.RepoTags,
            repoDigests = image.RepoDigests,
            sizeBytes = image.Size,
            created = image.Created
        }).ToList<object>();
    }

    public object GetTrustedRegistries()
    {
        return _guard.GetTrustedRegistries();
    }

    public async Task<object> PullImageAsync(string? image)
    {
        try
        {
            var normalizedImage = ValidateImage(image);
            var progress = await PullTrustedImageAsync(normalizedImage);
            return new { ok = true, image = normalizedImage, pulled = true, progress };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> RemoveImageAsync(string? image, bool force)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(image))
            {
                throw new DockerToolException("MISSING_REQUIRED_ARGUMENT", "An image id or tag is required.", "Pass image as a local image id or tag.");
            }

            var deleted = await _client.Images.DeleteImageAsync(
                image.Trim(),
                new ImageDeleteParameters
                {
                    Force = force,
                    NoPrune = false
                });

            return new { ok = true, image = image.Trim(), force, deleted };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> TagImageAsync(string? sourceImage, string? repository, string? tag)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceImage) || string.IsNullOrWhiteSpace(repository))
            {
                throw new DockerToolException("MISSING_REQUIRED_ARGUMENT", "sourceImage and repository are required.", "Pass a local source image plus repository and optional tag.");
            }

            await _client.Images.TagImageAsync(
                sourceImage.Trim(),
                new ImageTagParameters
                {
                    RepositoryName = repository.Trim(),
                    Tag = string.IsNullOrWhiteSpace(tag) ? "latest" : tag.Trim()
                });

            return new { ok = true, sourceImage = sourceImage.Trim(), repository = repository.Trim(), tag = string.IsNullOrWhiteSpace(tag) ? "latest" : tag.Trim(), tagged = true };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> BuildImageFromStorageAsync(string? contextPath, string? dockerfilePath, string? tag, bool noCache)
    {
        try
        {
            var contextFullPath = _storage.ResolveStoragePath(contextPath);
            if (!Directory.Exists(contextFullPath))
            {
                throw new DockerToolException("DIRECTORY_NOT_FOUND", "Build context directory was not found.", "Create files under storage first, then pass contextPath.");
            }

            var dockerfileRelativePath = string.IsNullOrWhiteSpace(dockerfilePath) ? "Dockerfile" : dockerfilePath.Trim().TrimStart('/');
            var dockerfileFullPath = Path.GetFullPath(Path.Combine(contextFullPath, dockerfileRelativePath));
            if (!File.Exists(dockerfileFullPath))
            {
                throw new DockerToolException("DOCKERFILE_NOT_FOUND", "Dockerfile was not found inside the build context.", "Pass dockerfilePath relative to contextPath.");
            }

            await ValidateDockerfileBaseImagesAsync(dockerfileFullPath);
            var tags = NormalizeTags(tag);
            await using var tar = TarBuilder.CreateFromDirectory(contextFullPath);
            var progress = await BuildImageAsync(tar, dockerfileRelativePath.Replace('\\', '/'), tags, noCache);
            return new { ok = true, contextPath, dockerfilePath = dockerfileRelativePath, tags, built = true, progress };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    public async Task<object> BuildImageFromDockerfileTextAsync(string? dockerfile, string? tag, bool noCache)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dockerfile))
            {
                throw new DockerToolException("MISSING_REQUIRED_ARGUMENT", "dockerfile is required.", "Pass the full Dockerfile text.");
            }

            ValidateDockerfileBaseImages(dockerfile);
            var tags = NormalizeTags(tag);
            await using var tar = TarBuilder.CreateSingleFile("Dockerfile", Encoding.UTF8.GetBytes(dockerfile));
            var progress = await BuildImageAsync(tar, "Dockerfile", tags, noCache);
            return new { ok = true, tags, built = true, progress };
        }
        catch (Exception ex)
        {
            return ToErrorResult(ex);
        }
    }

    internal async Task<IReadOnlyList<object>> BuildImageFromDockerfileTextForRunAsync(string dockerfile, string tag, bool noCache)
    {
        ValidateDockerfileBaseImages(dockerfile);
        await using var tar = TarBuilder.CreateSingleFile("Dockerfile", Encoding.UTF8.GetBytes(dockerfile));
        return await BuildImageAsync(tar, "Dockerfile", new[] { tag }, noCache);
    }

    private async Task<IReadOnlyList<object>> BuildImageAsync(Stream tar, string dockerfilePath, IReadOnlyList<string> tags, bool noCache)
    {
        var progressEvents = new List<object>();
        await _client.Images.BuildImageFromDockerfileAsync(
            new ImageBuildParameters
            {
                Dockerfile = dockerfilePath,
                Tags = tags.ToList(),
                NoCache = noCache,
                Remove = true,
                Pull = "true"
            },
            tar,
            Enumerable.Empty<AuthConfig>(),
            new Dictionary<string, string>(),
            new Progress<JSONMessage>(message =>
            {
                progressEvents.Add(new
                {
                    stream = message.Stream,
                    status = message.Status,
                    id = message.ID,
                    progress = message.ProgressMessage,
                    error = message.ErrorMessage
                });
            }),
            CancellationToken.None);

        return progressEvents;
    }

    private async Task<IReadOnlyList<object>> PullTrustedImageAsync(string image)
    {
        var progressEvents = new List<object>();
        await _client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = image },
            authConfig: null,
            progress: new Progress<JSONMessage>(message =>
            {
                progressEvents.Add(new
                {
                    status = message.Status,
                    id = message.ID,
                    progress = message.ProgressMessage,
                    error = message.ErrorMessage
                });
            }));
        return progressEvents;
    }

    private string ValidateImage(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            throw new DockerToolException("MISSING_REQUIRED_ARGUMENT", "A Docker image is required.", "Use a trusted registry image with a specific version tag when possible.");
        }

        var normalized = image.Trim();
        _guard.ValidateImage(normalized);
        return normalized;
    }

    private async Task ValidateDockerfileBaseImagesAsync(string dockerfilePath)
    {
        var dockerfile = await File.ReadAllTextAsync(dockerfilePath, Encoding.UTF8);
        ValidateDockerfileBaseImages(dockerfile);
    }

    private void ValidateDockerfileBaseImages(string dockerfile)
    {
        foreach (var line in dockerfile.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var image = parts.Skip(1).FirstOrDefault(part => !part.StartsWith("--", StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(image) && !string.Equals(image, "scratch", StringComparison.OrdinalIgnoreCase))
            {
                _guard.ValidateImage(image);
            }
        }
    }

    private static IReadOnlyList<string> NormalizeTags(string? tag)
    {
        var normalized = string.IsNullOrWhiteSpace(tag)
            ? $"mcp-built:{Guid.NewGuid():N}"
            : tag.Trim();
        return new[] { normalized };
    }

    private static object ToErrorResult(Exception ex)
    {
        return ex is DockerToolException toolException
            ? new { ok = false, errorCode = toolException.ErrorCode, message = toolException.Message, hint = toolException.Hint, acceptedArgs = toolException.AcceptedArgs }
            : new { ok = false, errorCode = "DOCKER_IMAGE_OPERATION_FAILED", message = ex.Message, hint = "Check Docker state and retry with corrected arguments.", acceptedArgs = (object?)null };
    }

    private static string ShortId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        return id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..19] : id[..Math.Min(12, id.Length)];
    }
}

internal static class TarBuilder
{
    public static MemoryStream CreateSingleFile(string name, byte[] bytes)
    {
        var stream = new MemoryStream();
        WriteFile(stream, name, bytes, DateTimeOffset.UtcNow);
        WriteEnd(stream);
        stream.Position = 0;
        return stream;
    }

    public static MemoryStream CreateFromDirectory(string directory)
    {
        var stream = new MemoryStream();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            WriteFile(stream, relative, File.ReadAllBytes(file), File.GetLastWriteTimeUtc(file));
        }

        WriteEnd(stream);
        stream.Position = 0;
        return stream;
    }

    private static void WriteFile(Stream stream, string name, byte[] bytes, DateTimeOffset modified)
    {
        var header = new byte[512];
        WriteAscii(header, 0, 100, name);
        WriteOctal(header, 100, 8, 0x1a4);
        WriteOctal(header, 108, 8, 0);
        WriteOctal(header, 116, 8, 0);
        WriteOctal(header, 124, 12, bytes.Length);
        WriteOctal(header, 136, 12, modified.ToUnixTimeSeconds());
        for (var i = 148; i < 156; i++)
        {
            header[i] = 0x20;
        }

        header[156] = (byte)'0';
        WriteAscii(header, 257, 6, "ustar");
        WriteAscii(header, 263, 2, "00");
        var checksum = header.Sum(b => (int)b);
        WriteOctal(header, 148, 8, checksum);
        stream.Write(header, 0, header.Length);
        stream.Write(bytes, 0, bytes.Length);

        var padding = 512 - bytes.Length % 512;
        if (padding < 512)
        {
            stream.Write(new byte[padding], 0, padding);
        }
    }

    private static void WriteEnd(Stream stream)
    {
        stream.Write(new byte[1024], 0, 1024);
    }

    private static void WriteAscii(byte[] buffer, int offset, int length, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, buffer, offset, Math.Min(bytes.Length, length));
    }

    private static void WriteOctal(byte[] buffer, int offset, int length, long value)
    {
        var text = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        WriteAscii(buffer, offset, length, text);
    }
}
