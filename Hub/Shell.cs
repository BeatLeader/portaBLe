using System.Diagnostics;
using System.Text;

namespace PortableHub;

public sealed class ShellException(string message, string output) : Exception(message)
{
    public string Output { get; } = output;
}

/// <summary>Runs external commands (git, dotnet, systemctl, sudo nginx) without a shell; arguments are passed as a list.</summary>
public static class Shell
{
    public static async Task<(int Code, string Output)> Run(string file, IEnumerable<string> args, string? cwd = null,
        TextWriter? log = null, IDictionary<string, string>? env = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;

        log?.WriteLine($"$ {file} {string.Join(' ', psi.ArgumentList)}");
        log?.Flush();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {file}");
        var output = new StringBuilder();
        void OnLine(string? line)
        {
            if (line == null) return;
            lock (output)
            {
                if (output.Length < 200_000) output.AppendLine(line);
                log?.WriteLine(line);
            }
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(20));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new ShellException($"{Path.GetFileName(file)} timed out or was cancelled", output.ToString());
        }
        p.WaitForExit(); // drain the async readers
        log?.Flush();
        lock (output) return (p.ExitCode, output.ToString());
    }

    /// <summary>Like <see cref="Run"/> but throws on a non-zero exit code.</summary>
    public static async Task<string> Check(string file, IEnumerable<string> args, string? cwd = null, TextWriter? log = null,
        IDictionary<string, string>? env = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var (code, output) = await Run(file, args, cwd, log, env, timeout, ct);
        if (code != 0)
        {
            var tail = string.Join('\n', output.Split('\n').TakeLast(12)).Trim();
            throw new ShellException($"{Path.GetFileName(file)} {args.FirstOrDefault()} failed (exit {code}): {tail}", output);
        }
        return output;
    }
}
