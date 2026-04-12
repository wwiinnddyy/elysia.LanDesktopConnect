using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Elysia.LanDesktopConnect.Services;

public class BunDetector
{
    public async Task<BunDetectionResult> DetectAsync()
    {
        // Windows
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await DetectOnWindowsAsync();
        }
        // Linux/macOS
        else
        {
            return await DetectOnUnixAsync();
        }
    }

    private async Task<BunDetectionResult> DetectOnWindowsAsync()
    {
        // 1. 检查 PATH
        var pathBun = FindInPath("bun.exe");
        if (!string.IsNullOrEmpty(pathBun))
        {
            var version = await GetBunVersionAsync(pathBun);
            if (version != null)
            {
                return BunDetectionResult.Found(pathBun, version);
            }
        }

        // 2. 检查常见位置
        var commonPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bun", "bin", "bun.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bun", "bin", "bun.exe"),
            @"C:\Program Files\bun\bun.exe",
            @"C:\Program Files (x86)\bun\bun.exe",
        };

        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
            {
                var version = await GetBunVersionAsync(path);
                if (version != null)
                {
                    return BunDetectionResult.Found(path, version);
                }
            }
        }

        return BunDetectionResult.NotFound();
    }

    private async Task<BunDetectionResult> DetectOnUnixAsync()
    {
        // 1. which bun
        var whichResult = await RunCommandAsync("which", "bun");
        if (whichResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(whichResult.Output))
        {
            var path = whichResult.Output.Trim();
            var version = await GetBunVersionAsync(path);
            if (version != null)
            {
                return BunDetectionResult.Found(path, version);
            }
        }

        // 2. 检查常见位置
        var commonPaths = new[]
        {
            "/usr/local/bin/bun",
            "/usr/bin/bun",
            "/opt/bun/bin/bun",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bun", "bin", "bun"),
        };

        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
            {
                var version = await GetBunVersionAsync(path);
                if (version != null)
                {
                    return BunDetectionResult.Found(path, version);
                }
            }
        }

        return BunDetectionResult.NotFound();
    }

    private string? FindInPath(string executable)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var paths = pathEnv.Split(Path.PathSeparator);
        foreach (var path in paths)
        {
            var fullPath = Path.Combine(path, executable);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    private async Task<string?> GetBunVersionAsync(string bunPath)
    {
        try
        {
            var result = await RunCommandAsync(bunPath, "--version");
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
            {
                return result.Output.Trim();
            }
        }
        catch
        {
            // 忽略错误
        }

        return null;
    }

    public async Task<bool> ValidateVersionAsync(string bunPath, Version minimumVersion)
    {
        var versionString = await GetBunVersionAsync(bunPath);
        if (string.IsNullOrEmpty(versionString)) return false;

        // 解析版本号 (例如 "1.1.3" 或 "1.1.3+abcdef")
        var versionPart = versionString.Split('+')[0].Trim();
        if (Version.TryParse(versionPart, out var version))
        {
            return version >= minimumVersion;
        }

        return false;
    }

    private async Task<CommandResult> RunCommandAsync(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CommandResult(process.ExitCode, output, error);
    }
}

public record BunDetectionResult
{
    public bool Found { get; init; }
    public string? Path { get; init; }
    public string? Version { get; init; }

    public static BunDetectionResult Found(string path, string version)
        => new() { Found = true, Path = path, Version = version };

    public static BunDetectionResult NotFound()
        => new() { Found = false };
}

public record CommandResult(int ExitCode, string Output, string Error);
