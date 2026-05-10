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

    public static IReadOnlyList<string> AllowedImageList => AllowedImages.Order(StringComparer.OrdinalIgnoreCase).ToList();

    public void ValidateImage(string image)
    {
        if (!AllowedImages.Contains(image))
        {
            throw new DockerToolException(
                "IMAGE_NOT_ALLOWED",
                $"Image not allowed: {image}.",
                $"Use one of the allowed images: {string.Join(", ", AllowedImages)}.",
                new { image = AllowedImageList });
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
