public sealed class DockerGuard
{
    private static readonly HashSet<string> AllowedImages = new(StringComparer.OrdinalIgnoreCase)
    {
        "nginx:alpine",
        "redis:7-alpine",
        "python:3.12-alpine",
        "alpine:latest",
        "hello-world:latest",
        "hello-world"
    };

    public void ValidateImage(string image)
    {
        if (!AllowedImages.Contains(image))
        {
            throw new InvalidOperationException(
                $"Image not allowed: {image}. Allowed images: {string.Join(", ", AllowedImages)}");
        }
    }

    public void ValidateResourceLimits(long memoryMb, double cpus)
    {
        if (memoryMb < 64 || memoryMb > 1024)
            throw new InvalidOperationException("Memory must be between 64 MB and 1024 MB.");

        if (cpus <= 0 || cpus > 2)
            throw new InvalidOperationException("CPU limit must be greater than 0 and no more than 2.");
    }
}
