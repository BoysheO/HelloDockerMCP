using System.Runtime.InteropServices;

public sealed class SystemService
{
    public object GetEnvironmentArchitecture()
    {
        return new
        {
            ok = true,
            architecture = NormalizeArchitecture(RuntimeInformation.OSArchitecture),
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            operatingSystem = RuntimeInformation.OSDescription
        };
    }

    private static string NormalizeArchitecture(Architecture architecture)
    {
        return architecture switch
        {
            Architecture.X86 or Architecture.X64 => "x86",
            Architecture.Arm or Architecture.Arm64 => "arm",
            _ => architecture.ToString().ToLowerInvariant()
        };
    }
}
