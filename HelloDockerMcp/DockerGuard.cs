using Microsoft.Extensions.Options;

public sealed class DockerGuard
{
    private readonly DockerOptions _options;
    private readonly HashSet<string> _trustedRegistries;

    public DockerGuard(IOptions<DockerOptions> options)
    {
        _options = options.Value;
        _trustedRegistries = new HashSet<string>(
            _options.TrustedRegistries.Select(NormalizeRegistry),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> TrustedRegistryList => _trustedRegistries
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public object GetTrustedRegistries()
    {
        return new
        {
            ok = true,
            trustedRegistries = TrustedRegistryList,
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
        var registry = GetRegistry(image);
        if (!_trustedRegistries.Contains(registry))
        {
            throw new DockerToolException(
                "REGISTRY_NOT_TRUSTED",
                $"Image registry is not trusted: {registry}.",
                $"Use an image from one of the trusted registries: {string.Join(", ", TrustedRegistryList)}.",
                new { trustedRegistries = TrustedRegistryList });
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

    private static string GetRegistry(string image)
    {
        var normalized = image.Trim();
        var slashIndex = normalized.IndexOf('/');
        if (slashIndex <= 0)
        {
            return "docker.io";
        }

        var firstSegment = normalized[..slashIndex];
        if (firstSegment.Contains('.') ||
            firstSegment.Contains(':') ||
            string.Equals(firstSegment, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeRegistry(firstSegment);
        }

        return "docker.io";
    }

    private static string NormalizeRegistry(string registry)
    {
        return registry.Trim().TrimEnd('/').ToLowerInvariant();
    }
}

public sealed class DockerOptions
{
    public List<string> TrustedRegistries { get; set; } =
    [
        "docker.io",
        "registry-1.docker.io",
        "harbor.boysheo.com"
    ];

    public DockerResourceLimitsOptions ResourceLimits { get; set; } = new();
}

public sealed class DockerResourceLimitsOptions
{
    public long MinMemoryMb { get; set; } = 64;

    public long MaxMemoryMb { get; set; } = 1024;

    public double MaxCpus { get; set; } = 2;
}
