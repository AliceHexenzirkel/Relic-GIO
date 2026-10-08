using System.Diagnostics;

namespace Relic.Core.Util;

/// <summary>Thin wrapper for running console tools (reg.exe, robocopy, docker, ssh, ...).</summary>
public static class Proc
{
    public readonly record struct Result(int Code, string Out, string Err)
    {
        public bool Ok => Code == 0;
    }

    /// <summary>Run <paramref name="exe"/> with args, capturing stdout/stderr. Never throws on a nonzero exit.</summary>
    public static Result Run(string exe, params string[] args)
        => RunIn(exe, workingDir: null, args);

    /// <summary>As <see cref="Run"/> but with an explicit working directory.</summary>
    public static Result RunIn(string exe, string? workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDir is not null) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exe}");
        // Read both streams concurrently to avoid pipe-buffer deadlock on chatty tools.
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return new Result(p.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
