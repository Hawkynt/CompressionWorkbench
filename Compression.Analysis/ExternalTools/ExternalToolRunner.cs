#pragma warning disable CS1591

using System.Diagnostics;

namespace Compression.Analysis.ExternalTools;

/// <summary>
/// Result of running an external tool process.
/// </summary>
public sealed class ToolRunResult {
  /// <summary>Process exit code.</summary>
  public required int ExitCode { get; init; }

  /// <summary>Standard output as text (UTF-8).</summary>
  public required string Stdout { get; init; }

  /// <summary>Standard error as text (UTF-8).</summary>
  public required string Stderr { get; init; }

  /// <summary>Standard output as raw bytes (only populated when <c>captureStdoutBytes</c> is true).</summary>
  public byte[]? StdoutBytes { get; init; }

  /// <summary>Whether the process was killed due to timeout.</summary>
  public bool TimedOut { get; init; }

  /// <summary>True when the process exited with code 0.</summary>
  public bool Success => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// Generic static utility to invoke external command-line tools with redirected I/O,
/// optional stdin piping, timeout handling, and cross-platform support.
/// </summary>
public static class ExternalToolRunner {

  /// <summary>Default timeout in milliseconds (30 seconds).</summary>
  public const int DefaultTimeoutMs = 30_000;

  /// <summary>
  /// Runs an external tool asynchronously, capturing its stdout and stderr.
  /// </summary>
  /// <param name="executable">Path or name of the executable (resolved via PATH if not absolute).</param>
  /// <param name="arguments">Command-line arguments.</param>
  /// <param name="stdinData">Optional data to pipe into the process's standard input.</param>
  /// <param name="timeoutMs">Maximum time to wait for the process to exit.</param>
  /// <param name="captureStdoutBytes">When true, also captures stdout as raw bytes.</param>
  /// <param name="workingDirectory">Optional working directory for the process.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A <see cref="ToolRunResult"/> with captured output and exit code.</returns>
  public static async Task<ToolRunResult> RunAsync(
    string executable,
    string arguments,
    byte[]? stdinData = null,
    int timeoutMs = DefaultTimeoutMs,
    bool captureStdoutBytes = false,
    string? workingDirectory = null,
    CancellationToken cancellationToken = default
  ) {
    var psi = new ProcessStartInfo {
      FileName = executable,
      Arguments = arguments,
      RedirectStandardInput = stdinData != null,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };

    if (workingDirectory != null)
      psi.WorkingDirectory = workingDirectory;

    Process process;
    try {
      process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start process: {executable}");
    } catch (Exception ex) {
      return new ToolRunResult {
        ExitCode = -1,
        Stdout = string.Empty,
        Stderr = $"Failed to start '{executable}': {ex.Message}",
        TimedOut = false
      };
    }

    using (process) {
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      cts.CancelAfter(timeoutMs);

      // Write stdin if provided.
      if (stdinData != null) {
        try {
          await process.StandardInput.BaseStream.WriteAsync(stdinData, cts.Token).ConfigureAwait(false);
          process.StandardInput.Close();
        } catch (OperationCanceledException) {
          // Timeout or cancellation during stdin write — fall through to kill.
        }
      }

      // Read stdout and stderr concurrently.
      var stdoutTask = captureStdoutBytes
        ? ReadAllBytesAsync(process.StandardOutput.BaseStream, cts.Token)
        : Task.FromResult<byte[]?>(null);

      var stdoutTextTask = captureStdoutBytes
        ? Task.FromResult(string.Empty)
        : process.StandardOutput.ReadToEndAsync(cts.Token);

      var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

      try {
        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

        var stdoutBytes = await stdoutTask.ConfigureAwait(false);
        var stdoutText = await stdoutTextTask.ConfigureAwait(false);
        var stderrText = await stderrTask.ConfigureAwait(false);

        if (captureStdoutBytes && stdoutBytes != null)
          stdoutText = System.Text.Encoding.UTF8.GetString(stdoutBytes);

        return new ToolRunResult {
          ExitCode = process.ExitCode,
          Stdout = stdoutText,
          Stderr = stderrText,
          StdoutBytes = stdoutBytes,
          TimedOut = false
        };
      } catch (OperationCanceledException) {
        // Timeout or cancellation — kill the process.
        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }

        // Drain whatever we can.
        var partialStdout = string.Empty;
        var partialStderr = string.Empty;
        try { partialStdout = await process.StandardOutput.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
        try { partialStderr = await process.StandardError.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }

        return new ToolRunResult {
          ExitCode = -1,
          Stdout = partialStdout,
          Stderr = partialStderr,
          TimedOut = true
        };
      }
    }
  }

  /// <summary>
  /// Synchronous convenience overload for <see cref="RunAsync"/>.
  /// </summary>
  public static ToolRunResult Run(
    string executable,
    string arguments,
    byte[]? stdinData = null,
    int timeoutMs = DefaultTimeoutMs,
    bool captureStdoutBytes = false,
    string? workingDirectory = null
  ) => RunAsync(executable, arguments, stdinData, timeoutMs, captureStdoutBytes, workingDirectory).GetAwaiter().GetResult();

  private static async Task<byte[]?> ReadAllBytesAsync(Stream stream, CancellationToken ct) {
    using var ms = new MemoryStream();
    await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
    return ms.ToArray();
  }
}
