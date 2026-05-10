using Docker.DotNet;
using Docker.DotNet.Models;
using System.Text;

public sealed class DockerService
{
    private readonly DockerClient _client;
    private readonly DockerGuard _guard;

    public DockerService(DockerGuard guard)
    {
        _guard = guard;

        // Docker.DotNet 可以自动连接本地 Docker。
        // Windows 通常是 npipe://./pipe/docker_engine
        // Linux/macOS 通常是 unix:///var/run/docker.sock
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        _client = string.IsNullOrWhiteSpace(dockerHost)
            ? new DockerClientConfiguration().CreateClient()
            : new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();
    }

    public async Task<IReadOnlyList<object>> ListManagedContainersAsync()
    {
        var containers = await _client.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool>
                    {
                        [_guard.ManagedLabelFilter()] = true
                    }
                }
            });

        return containers.Select(c => new
        {
            id = c.ID[..Math.Min(12, c.ID.Length)],
            names = c.Names,
            image = c.Image,
            state = c.State,
            status = c.Status,
            ports = c.Ports
        }).ToList();
    }

    public async Task<object> CreateContainerAsync(
        string image,
        string name,
        string? command,
        long memoryMb,
        double cpus)
    {
        _guard.ValidateImage(image);
        _guard.ValidateContainerName(name);
        _guard.ValidateResourceLimits(memoryMb, cpus);

        // 可选：先拉镜像。生产环境建议加 registry 白名单。
        await _client.Images.CreateImageAsync(
            new ImagesCreateParameters
            {
                FromImage = image
            },
            authConfig: null,
            progress: new Progress<JSONMessage>());

        var createParams = new CreateContainerParameters
        {
            Image = image,
            Name = name,
            Cmd = string.IsNullOrWhiteSpace(command)
                ? null
                : SplitCommand(command),

            Labels = new Dictionary<string, string>
            {
                [DockerGuard.ManagedLabelKey] = DockerGuard.ManagedLabelValue
            },

            HostConfig = new HostConfig
            {
                Memory = memoryMb * 1024L * 1024L,
                NanoCPUs = (long)(cpus * 1_000_000_000L),

                // 不允许特权容器
                Privileged = false,

                // 不自动删除，方便审计和手动控制
                AutoRemove = false,

                // 默认 bridge，不允许 host network
                NetworkMode = "bridge",

                // 禁止提升权限
                SecurityOpt = new List<string>
                {
                    "no-new-privileges:true"
                },

                // 默认不做任何宿主机目录绑定
                Binds = new List<string>()
            }
        };

        var result = await _client.Containers.CreateContainerAsync(createParams);

        return new
        {
            id = result.ID[..Math.Min(12, result.ID.Length)],
            name,
            image,
            created = true
        };
    }

    public async Task<object> StartContainerAsync(string name)
    {
        var container = await GetManagedContainerByNameAsync(name);

        var started = await _client.Containers.StartContainerAsync(
            container.ID,
            new ContainerStartParameters());

        return new
        {
            id = container.ID[..Math.Min(12, container.ID.Length)],
            name,
            started
        };
    }

    public async Task<object> StopContainerAsync(string name)
    {
        var container = await GetManagedContainerByNameAsync(name);

        var stopped = await _client.Containers.StopContainerAsync(
            container.ID,
            new ContainerStopParameters
            {
                WaitBeforeKillSeconds = 10
            });

        return new
        {
            id = container.ID[..Math.Min(12, container.ID.Length)],
            name,
            stopped
        };
    }

    public async Task<object> RemoveContainerAsync(string name, bool force = false)
    {
        var container = await GetManagedContainerByNameAsync(name);

        await _client.Containers.RemoveContainerAsync(
            container.ID,
            new ContainerRemoveParameters
            {
                Force = force,
                RemoveVolumes = false
            });

        return new
        {
            id = container.ID[..Math.Min(12, container.ID.Length)],
            name,
            removed = true,
            force
        };
    }

public async Task<string> GetLogsAsync(string name, int tail)
{
    var container = await GetManagedContainerByNameAsync(name);

    tail = Math.Clamp(tail, 1, 500);

    using var stream = await _client.Containers.GetContainerLogsAsync(
        container.ID,
        tty: false,
        parameters: new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Tail = tail.ToString(),
            Timestamps = true,
            Follow = false
        });

    var result = await stream.ReadOutputToEndAsync(CancellationToken.None);

    var output = new StringBuilder();

    if (!string.IsNullOrWhiteSpace(result.stdout))
    {
        output.AppendLine("STDOUT:");
        output.AppendLine(result.stdout);
    }

    if (!string.IsNullOrWhiteSpace(result.stderr))
    {
        output.AppendLine("STDERR:");
        output.AppendLine(result.stderr);
    }

    return output.Length == 0
        ? string.Empty
        : output.ToString();
}

    private async Task<ContainerListResponse> GetManagedContainerByNameAsync(string name)
    {
        _guard.ValidateContainerName(name);

        var containers = await _client.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["name"] = new Dictionary<string, bool>
                    {
                        [name] = true
                    },
                    ["label"] = new Dictionary<string, bool>
                    {
                        [_guard.ManagedLabelFilter()] = true
                    }
                }
            });

        var container = containers.FirstOrDefault();

        if (container is null)
            throw new InvalidOperationException($"Managed container not found: {name}");

        return container;
    }

    private static IList<string> SplitCommand(string command)
    {
        // 简化版：适合 "sleep 60" 这类简单命令。
        // 生产环境建议使用更严格的参数数组，而不是让 AI 输入一整条 shell 字符串。
        return command
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
