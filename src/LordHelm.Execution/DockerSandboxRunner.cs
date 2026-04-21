using System.Diagnostics;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using LordHelm.Core;
using Microsoft.Extensions.Logging;

namespace LordHelm.Execution;

/// <summary>
/// Ephemeral-container runner using Docker.DotNet. Hardening defaults:
/// CapDrop=ALL, ReadonlyRootfs, NetworkDisabled, PidsLimit, NanoCpus, Memory cap,
/// bind-mount allow-list only. Images must be pinned by digest. Containers are
/// removed unconditionally in a finally block.
/// </summary>
public sealed class DockerSandboxRunner : ISandboxRunner, IAsyncDisposable
{
    private readonly IDockerClient _client;
    private readonly ILogger<DockerSandboxRunner> _logger;

    public DockerSandboxRunner(IDockerClient client, ILogger<DockerSandboxRunner> logger)
    {
        _client = client;
        _logger = logger;
    }

    public static DockerSandboxRunner CreateDefault(ILogger<DockerSandboxRunner> logger)
    {
        var uri = OperatingSystem.IsWindows()
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");
        var client = new DockerClientConfiguration(uri).CreateClient();
        return new DockerSandboxRunner(client, logger);
    }

    public async Task<SandboxResult> RunAsync(string[] command, SandboxPolicy policy, CancellationToken ct = default)
    {
        ValidateImageRef(policy.ImageRefWithDigest);

        string? id = null;
        var sw = Stopwatch.StartNew();
        try
        {
            var tmpfs = policy.TmpfsMounts ?? new Dictionary<string, string>();
            var binds = (policy.ReadOnlyBinds ?? Array.Empty<string>())
                .Select(b => b + ":ro")
                .ToList();

            var create = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = policy.ImageRefWithDigest,
                Cmd = command,
                AttachStdout = true,
                AttachStderr = true,
                WorkingDir = "/work",
                HostConfig = new HostConfig
                {
                    CapDrop = new List<string> { "ALL" },
                    ReadonlyRootfs = policy.ReadonlyRootfs,
                    NetworkMode = policy.NetworkDisabled ? "none" : "bridge",
                    Memory = policy.MemoryBytes,
                    NanoCPUs = policy.NanoCpus,
                    PidsLimit = policy.PidsLimit,
                    Tmpfs = new Dictionary<string, string>(tmpfs),
                    Binds = binds,
                    AutoRemove = false,
                },
            }, ct);
            id = create.ID;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (policy.WallClockTimeout is { } w) linked.CancelAfter(w);

            await _client.Containers.StartContainerAsync(id, new ContainerStartParameters(), linked.Token);

            var waitTask = _client.Containers.WaitContainerAsync(id, linked.Token);

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var logStream = await _client.Containers.GetContainerLogsAsync(id, tty: false,
                new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = true, Timestamps = false },
                linked.Token);
            var muxRead = logStream.CopyOutputToAsync(
                null,
                new BufferedStreamWriter(stdout),
                new BufferedStreamWriter(stderr),
                linked.Token);

            ContainerWaitResponse resp;
            try
            {
                await Task.WhenAny(waitTask, Task.Delay(Timeout.Infinite, linked.Token));
                resp = await waitTask;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Sandbox wall-clock timeout; killing container {Id}", id);
                try { await _client.Containers.KillContainerAsync(id, new ContainerKillParameters { Signal = "SIGKILL" }, CancellationToken.None); } catch { }
                resp = new ContainerWaitResponse { StatusCode = 137 };
            }

            try { await muxRead; } catch { }

            return new SandboxResult((int)resp.StatusCode, stdout.ToString(), stderr.ToString(), sw.Elapsed);
        }
        finally
        {
            if (id is not null)
            {
                try
                {
                    await _client.Containers.RemoveContainerAsync(id,
                        new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove sandbox container {Id}", id);
                }
            }
        }
    }

    private static void ValidateImageRef(string imageRef)
    {
        if (!imageRef.Contains('@'))
            throw new InvalidOperationException($"Sandbox image '{imageRef}' must be pinned by digest (e.g. 'python:3.12-slim@sha256:...').");
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is IAsyncDisposable ad) await ad.DisposeAsync();
        else _client.Dispose();
    }

    private sealed class BufferedStreamWriter : System.IO.Stream
    {
        private readonly StringBuilder _sb;
        public BufferedStreamWriter(StringBuilder sb) => _sb = sb;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
            => _sb.Append(Encoding.UTF8.GetString(buffer, offset, count));
    }
}
