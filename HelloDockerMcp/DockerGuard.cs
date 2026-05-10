using Microsoft.Extensions.Options;

public sealed class DockerGuard
{
    private readonly DockerOptions _options;
    private readonly HashSet<string> _allowedImageNames;

    public DockerGuard(IOptions<DockerOptions> options)
    {
        _options = options.Value;
        _allowedImageNames = new HashSet<string>(
            _options.AllowedImages.Select(NormalizeImageName),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> AllowedImageList => _options.AllowedImages
        .Select(image => image.Trim())
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public IReadOnlyList<string> AllowedImageNameList => _allowedImageNames
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public object GetAvailableImages()
    {
        return new
        {
            ok = true,
            images = AllowedImageList,
            imageNames = AllowedImageNameList,
            resourceLimits = new
            {
                memoryMb = new
                {
                    minimum = _options.ResourceLimits.MinMemoryMb,
                    maximum = _options.ResourceLimits.MaxMemoryMb
                },
                cpus = new
                {
                    minimumExclusive = 0,
                    maximum = _options.ResourceLimits.MaxCpus
                }
            }
        };
    }

    public void ValidateImage(string image)
    {
        var imageName = NormalizeImageName(image);
        if (!_allowedImageNames.Contains(imageName))
        {
            throw new DockerToolException(
                "IMAGE_NOT_ALLOWED",
                $"Image not allowed: {image}.",
                $"Use one of the allowed image names: {string.Join(", ", AllowedImageNameList)}.",
                new { imageName = AllowedImageNameList });
        }
    }

    public void ValidateResourceLimits(long memoryMb, double cpus)
    {
        var limits = _options.ResourceLimits;
        if (memoryMb < limits.MinMemoryMb || memoryMb > limits.MaxMemoryMb)
        {
            throw new DockerToolException(
                "INVALID_ARGUMENT",
                $"Memory must be between {limits.MinMemoryMb} MB and {limits.MaxMemoryMb} MB.",
                "Use a memoryMb value within the configured range.",
                new
                {
                    memoryMb = new
                    {
                        minimum = limits.MinMemoryMb,
                        maximum = limits.MaxMemoryMb
                    }
                });
        }

        if (cpus <= 0 || cpus > limits.MaxCpus)
        {
            throw new DockerToolException(
                "INVALID_ARGUMENT",
                $"CPU limit must be greater than 0 and no more than {limits.MaxCpus}.",
                "Use a cpus value within the configured range.",
                new
                {
                    cpus = new
                    {
                        minimumExclusive = 0,
                        maximum = limits.MaxCpus
                    }
                });
        }
    }

    private static string NormalizeImageName(string image)
    {
        var normalized = image.Trim();
        var digestIndex = normalized.IndexOf('@', StringComparison.Ordinal);
        if (digestIndex >= 0)
        {
            normalized = normalized[..digestIndex];
        }

        var lastSlashIndex = normalized.LastIndexOf('/');
        var lastColonIndex = normalized.LastIndexOf(':');
        if (lastColonIndex > lastSlashIndex)
        {
            normalized = normalized[..lastColonIndex];
        }

        return normalized;
    }
}

public sealed class DockerOptions
{
    public List<string> AllowedImages { get; set; } =
    [
        "nginx:alpine",
        "redis:7-alpine",
        "python:3.12-alpine",
        "alpine:latest",
        "hello-world:latest",
        "hello-world"
    ];

    public DockerResourceLimitsOptions ResourceLimits { get; set; } = new();
}

public sealed class DockerResourceLimitsOptions
{
    public long MinMemoryMb { get; set; } = 64;

    public long MaxMemoryMb { get; set; } = 1024;

    public double MaxCpus { get; set; } = 2;
}
