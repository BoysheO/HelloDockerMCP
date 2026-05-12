using Microsoft.Extensions.Options;

public sealed class DockerGuard
{
    private readonly IOptionsMonitor<DockerOptions> _options;

    public DockerGuard(IOptionsMonitor<DockerOptions> options)
    {
        _options = options;
    }

    public IReadOnlyList<string> TrustedRegistryList => GetTrustedRegistrySet()
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public object GetTrustedRegistries()
    {
        var options = _options.CurrentValue;
        return new
        {
            ok = true,
            trustedRegistries = TrustedRegistryList,
            resourceLimits = new
            {
                memoryMb = new
                {
                    minimum = options.ResourceLimits.MinMemoryMb,
                    maximum = options.ResourceLimits.MaxMemoryMb
                },
                cpus = new
                {
                    minimumExclusive = 0,
                    maximum = options.ResourceLimits.MaxCpus
                }
            }
        };
    }

    public void ValidateImage(string image)
    {
        var registry = GetRegistry(image);
        var trustedRegistries = GetTrustedRegistrySet();
        if (!trustedRegistries.Contains(registry))
        {
            var trustedRegistryList = trustedRegistries
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            throw new DockerToolException(
                "REGISTRY_NOT_TRUSTED",
                $"Image registry is not trusted: {registry}.",
                $"Use an image from one of the trusted registries: {string.Join(", ", trustedRegistryList)}.",
                new { trustedRegistries = trustedRegistryList });
        }
    }

    public void ValidateResourceLimits(long memoryMb, double cpus)
    {
        var limits = _options.CurrentValue.ResourceLimits;
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

    private HashSet<string> GetTrustedRegistrySet()
    {
        return new HashSet<string>(
            _options.CurrentValue.TrustedRegistries
                .Where(registry => !string.IsNullOrWhiteSpace(registry))
                .Select(NormalizeRegistry),
            StringComparer.OrdinalIgnoreCase);
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
