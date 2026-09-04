using System.Runtime.InteropServices;

namespace Compression.Mounting.Fuse;

public sealed record FuseRuntimeStatus(
  bool IsAvailable,
  string? LibraryName,
  string? FusermountPath,
  string? UnavailableReason
);

public static class FuseRuntimeProbe {
  internal const string RuntimeLibrary = "libfuse3.so.3";

  public static FuseRuntimeStatus Probe() {
    if (!OperatingSystem.IsLinux())
      return Unavailable("FUSE3 mounting is available only on Linux.");

    if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
      return Unavailable($"The current FUSE3 interop layout is qualified for Linux x64, not {RuntimeInformation.ProcessArchitecture}.");

    if (!File.Exists("/dev/fuse"))
      return Unavailable("The FUSE kernel device '/dev/fuse' is unavailable on this host.");

    if (!NativeLibrary.TryLoad(RuntimeLibrary, out var libraryHandle))
      return Unavailable($"The FUSE3 runtime library '{RuntimeLibrary}' could not be loaded.");

    NativeLibrary.Free(libraryHandle);

    var fusermount = FindExecutableOnPath("fusermount3");
    if (fusermount is null)
      return Unavailable("'fusermount3' was not found on PATH; non-root FUSE mounting cannot be established safely.", RuntimeLibrary);

    return new(true, RuntimeLibrary, fusermount, null);
  }

  internal static string? FindExecutableOnPath(string executableName, string? path = null) {
    ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

    path ??= Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrWhiteSpace(path))
      return null;

    foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var candidate = Path.Combine(directory, executableName);
      if (File.Exists(candidate))
        return candidate;
    }

    return null;
  }

  private static FuseRuntimeStatus Unavailable(string reason, string? libraryName = null)
    => new(false, libraryName, null, reason);
}
