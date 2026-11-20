using System.Diagnostics;

namespace Compression.Tests;

/// <summary>
/// Reads an external tool's redirected output safely. Reading standard output to the end and only
/// then standard error deadlocks as soon as the tool fills the standard-error pipe: the tool blocks
/// writing, the test blocks reading. These helpers drain both streams at once and stop a tool that
/// runs past its time limit, killing its whole process tree, so a test fails with the command's name
/// instead of hanging.
/// </summary>
internal static class ProcessOutput {
  /// <summary>How long a tool may run when the caller gives no limit.</summary>
  public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

  /// <summary>
  /// Waits for <paramref name="process"/> to exit while draining its redirected standard output and
  /// standard error, and returns both.
  /// </summary>
  /// <param name="process">A started process; a stream that is not redirected reads as empty.</param>
  /// <param name="timeout">The time limit; <see cref="DefaultTimeout"/> when null.</param>
  /// <exception cref="TimeoutException">The process ran past the limit; it has been killed with its process tree.</exception>
  public static (string StdOut, string StdErr) ReadOutput(this Process process, TimeSpan? timeout = null) {
    ArgumentNullException.ThrowIfNull(process);
    var stdout = process.StartInfo.RedirectStandardOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
    var stderr = process.StartInfo.RedirectStandardError ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
    WaitOrKill(process, timeout ?? DefaultTimeout);
    return (stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
  }

  /// <summary><see cref="ReadOutput"/> for a tool whose standard output is binary data.</summary>
  /// <exception cref="TimeoutException">The process ran past the limit; it has been killed with its process tree.</exception>
  public static (byte[] StdOut, string StdErr) ReadOutputBytes(this Process process, TimeSpan? timeout = null) {
    ArgumentNullException.ThrowIfNull(process);
    var captured = new MemoryStream();
    var stdout = process.StandardOutput.BaseStream.CopyToAsync(captured);
    var stderr = process.StartInfo.RedirectStandardError ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
    WaitOrKill(process, timeout ?? DefaultTimeout);
    stdout.GetAwaiter().GetResult();
    return (captured.ToArray(), stderr.GetAwaiter().GetResult());
  }

  private static void WaitOrKill(Process process, TimeSpan limit) {
    if (process.WaitForExit(limit)) {
      process.WaitForExit(); // let the asynchronous readers see the end of both streams
      return;
    }
    var command = Describe(process);
    try {
      process.Kill(entireProcessTree: true);
    } catch (InvalidOperationException) {
      // it exited in the meantime
    }
    process.WaitForExit(5000);
    throw new TimeoutException($"'{command}' did not finish within {limit.TotalSeconds:0} s and was killed.");
  }

  /// <summary><see cref="ReadOutput"/>, standard output followed by standard error as one string.</summary>
  public static string ReadOutputJoined(this Process process, TimeSpan? timeout = null) {
    var (stdout, stderr) = process.ReadOutput(timeout);
    return stdout + stderr;
  }

  private static string Describe(Process process) {
    try {
      var info = process.StartInfo;
      var arguments = info.ArgumentList.Count > 0 ? string.Join(' ', info.ArgumentList) : info.Arguments;
      return string.IsNullOrEmpty(arguments) ? info.FileName : $"{info.FileName} {arguments}";
    } catch (InvalidOperationException) {
      return "the external process";
    }
  }
}
