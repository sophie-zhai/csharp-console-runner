using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        var origins = builder.Configuration.GetSection("AllowedOrigins")
            .GetChildren()
            .Select(x => x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray();
        if (origins.Length == 0)
            policy.AllowAnyOrigin();
        else
            policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("runs", limiter =>
    {
        limiter.PermitLimit = 12;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var app = builder.Build();
app.UseCors("Frontend");
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/run", async Task<Results<Ok<RunResponse>, BadRequest<RunResponse>>> (RunRequest request, IConfiguration config, CancellationToken cancellationToken) =>
{
    const int maxSourceChars = 40_000;
    const int maxOutputChars = 8_000;

    if (string.IsNullOrWhiteSpace(request.Code))
        return TypedResults.BadRequest(new RunResponse("invalid", ""));

    if (request.Code.Length > maxSourceChars)
        return TypedResults.BadRequest(new RunResponse("invalid", ""));

    var docker = config["DockerCommand"] ?? "docker";
    var sdkImage = config["RunnerImage"] ?? "mcr.microsoft.com/dotnet/sdk:10.0";
    var workRoot = Path.GetFullPath(config["WorkRoot"] ?? Path.Combine(AppContext.BaseDirectory, "runner-work"));
    Directory.CreateDirectory(workRoot);

    var runId = Guid.NewGuid().ToString("N");
    var workDir = Path.Combine(workRoot, runId);
    Directory.CreateDirectory(workDir);

    try
    {
        await File.WriteAllTextAsync(Path.Combine(workDir, "Program.cs"), request.Code, new UTF8Encoding(false), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workDir, "Student.csproj"), StudentProject, new UTF8Encoding(false), cancellationToken);

        // Compile in a disposable container. No network, fixed resources, fixed project file.
        var compileContainer = $"csharp-compile-{runId}";
        var compile = await RunDockerAsync(
            docker,
            ["run", "--rm", "--name", compileContainer, "--network", "none", "--memory", "768m", "--cpus", "1.0", "--pids-limit", "96",
             "--security-opt", "no-new-privileges", "--cap-drop", "ALL",
             "--mount", $"type=bind,src={workDir},dst=/workspace",
             "-w", "/workspace", sdkImage,
             "dotnet", "build", "Student.csproj", "-c", "Release", "--nologo", "--verbosity", "quiet"],
            TimeSpan.FromSeconds(20), cancellationToken, cleanupContainerName: compileContainer);

        if (compile.TimedOut)
            return TypedResults.Ok(new RunResponse("timeout", ""));

        if (compile.ExitCode != 0)
            return TypedResults.Ok(new RunResponse("compile_error", ""));

        // Execute only the built assembly, in a new more-restricted container.
        var runContainer = $"csharp-run-{runId}";
        var execute = await RunDockerAsync(
            docker,
            ["run", "--rm", "--name", runContainer, "--network", "none", "--read-only", "--memory", "256m", "--cpus", "0.75", "--pids-limit", "48",
             "--security-opt", "no-new-privileges", "--cap-drop", "ALL",
             "--tmpfs", "/tmp:rw,noexec,nosuid,size=32m",
             "--mount", $"type=bind,src={workDir},dst=/workspace,readonly",
             "-w", "/workspace", "mcr.microsoft.com/dotnet/runtime:10.0",
             "dotnet", "/workspace/bin/Release/net10.0/Student.dll"],
            TimeSpan.FromSeconds(4), cancellationToken,
            maxOutputChars, runContainer);

        if (execute.TimedOut)
            return TypedResults.Ok(new RunResponse("timeout", ""));

        if (execute.ExitCode != 0)
            return TypedResults.Ok(new RunResponse("runtime_error", ""));

        return TypedResults.Ok(new RunResponse("success", SanitizeOutput(execute.StdOut, maxOutputChars)));
    }
    finally
    {
        try { Directory.Delete(workDir, recursive: true); } catch { }
    }
}).RequireRateLimiting("runs");

app.Run();

static async Task<ProcessResult> RunDockerAsync(
    string executable,
    IReadOnlyList<string> args,
    TimeSpan timeout,
    CancellationToken outerToken,
    int outputLimit = 16_000,
    string? cleanupContainerName = null)
{
    var psi = new ProcessStartInfo
    {
        FileName = executable,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    foreach (var arg in args)
        psi.ArgumentList.Add(arg);

    using var process = new Process { StartInfo = psi };
    process.Start();

    var stdoutTask = ReadLimitedAsync(process.StandardOutput, outputLimit, outerToken);
    var stderrTask = ReadLimitedAsync(process.StandardError, outputLimit, outerToken);

    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
    timeoutCts.CancelAfter(timeout);

    try
    {
        await process.WaitForExitAsync(timeoutCts.Token);
        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask, false);
    }
    catch (OperationCanceledException)
    {
        try { process.Kill(entireProcessTree: true); } catch { }
        if (!string.IsNullOrWhiteSpace(cleanupContainerName))
            await ForceRemoveContainerAsync(executable, cleanupContainerName);
        if (outerToken.IsCancellationRequested)
            throw;
        return new ProcessResult(-1, "", "", true);
    }
}

static async Task ForceRemoveContainerAsync(string docker, string containerName)
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = docker,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("rm");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(containerName);
        using var cleanup = Process.Start(psi);
        if (cleanup is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await cleanup.WaitForExitAsync(cts.Token); } catch { }
        }
    }
    catch { }
}

static async Task<string> ReadLimitedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
{
    var buffer = new char[1024];
    var sb = new StringBuilder(Math.Min(limit, 4096));

    while (true)
    {
        var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        if (read <= 0) break;

        if (sb.Length < limit)
        {
            var take = Math.Min(read, limit - sb.Length);
            sb.Append(buffer, 0, take);
        }
    }

    return sb.ToString();
}

static string SanitizeOutput(string value, int maxChars)
{
    if (string.IsNullOrEmpty(value)) return "";
    var cleaned = value.Replace("\0", "");
    if (cleaned.Length <= maxChars) return cleaned;
    return cleaned[..maxChars] + "\n[output truncated]";
}

const string StudentProject = """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
""";

record RunRequest(string Code);
record RunResponse(string Status, string Output);
record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);
