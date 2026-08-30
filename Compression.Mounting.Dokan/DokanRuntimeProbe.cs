using System.Runtime.InteropServices;

namespace Compression.Mounting.Dokan;

public sealed record DokanRuntimeStatus(
  bool IsAvailable,
  uint LibraryVersion,
  uint DriverVersion,
  string? LibraryPath,
  string? UnavailableReason
);

/// <summary>
/// Probes the native Dokany 2 user-mode library and its kernel driver without
/// registering a filesystem or requiring administrator privileges.
/// </summary>
public static class DokanRuntimeProbe {
  private const string LibraryFileName = "dokan2.dll";
  private const string LibraryVersionExport = "DokanVersion";
  private const string DriverVersionExport = "DokanDriverVersion";

  [UnmanagedFunctionPointer(CallingConvention.Winapi)]
  private delegate uint VersionProcedure();

  public static DokanRuntimeStatus Probe() {
    if (!OperatingSystem.IsWindows())
      return Unavailable("Dokan is a Windows-only mount backend.");

    var candidates = new[] {
      Path.Combine(AppContext.BaseDirectory, LibraryFileName),
      Path.Combine(Environment.SystemDirectory, LibraryFileName),
    }.Distinct(StringComparer.OrdinalIgnoreCase);

    string? lastFailure = null;
    foreach (var candidate in candidates) {
      if (!File.Exists(candidate)) continue;

      IntPtr library = IntPtr.Zero;
      try {
        if (!NativeLibrary.TryLoad(candidate, out library)) {
          lastFailure = $"Found '{candidate}' but the native loader rejected it.";
          continue;
        }

        if (!TryGetVersionProcedure(library, LibraryVersionExport, out var libraryVersionProcedure))
          return Unavailable($"'{candidate}' does not export {LibraryVersionExport}().", candidate);
        if (!TryGetVersionProcedure(library, DriverVersionExport, out var driverVersionProcedure))
          return Unavailable($"'{candidate}' does not export {DriverVersionExport}().", candidate);

        var libraryVersion = libraryVersionProcedure();
        var driverVersion = driverVersionProcedure();
        if (libraryVersion == 0)
          return new(false, 0, driverVersion, candidate, "DokanVersion() returned 0.");
        if (driverVersion == 0)
          return new(
            false,
            libraryVersion,
            0,
            candidate,
            "The Dokan user-mode library is present, but DokanDriverVersion() returned 0; the Dokan 2 driver is unavailable or could not be queried."
          );

        return new(true, libraryVersion, driverVersion, candidate, null);
      } catch (BadImageFormatException ex) {
        lastFailure = $"'{candidate}' has the wrong architecture or is not a valid native library: {ex.Message}";
      } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) {
        lastFailure = $"Unable to use '{candidate}': {ex.Message}";
      } finally {
        if (library != IntPtr.Zero)
          NativeLibrary.Free(library);
      }
    }

    return Unavailable(lastFailure ?? $"{LibraryFileName} was not found beside the application or in the Windows system directory.");
  }

  private static bool TryGetVersionProcedure(IntPtr library, string export, out VersionProcedure procedure) {
    if (NativeLibrary.TryGetExport(library, export, out var address)) {
      procedure = Marshal.GetDelegateForFunctionPointer<VersionProcedure>(address);
      return true;
    }

    procedure = null!;
    return false;
  }

  private static DokanRuntimeStatus Unavailable(string reason, string? libraryPath = null)
    => new(false, 0, 0, libraryPath, reason);
}
