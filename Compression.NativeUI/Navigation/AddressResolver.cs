using Compression.Lib;
using Compression.Registry;

namespace Compression.NativeUI.Navigation;

/// <summary>Works out where a typed or pasted address leads.</summary>
/// <remarks>
/// An address may run straight through an archive: <c>C:\dl\a.zip\docs</c> means the <c>docs</c>
/// folder inside <c>a.zip</c>. Nothing on disk is called that, so the resolver walks the address
/// from the front, finds the first component that is a file, and treats the remainder as a folder
/// inside it — provided that file is an archive at all.
/// </remarks>
internal static class AddressResolver {
  /// <summary>Returns the location <paramref name="typed"/> names, or null when it names nothing.</summary>
  public static Location? Resolve(string? typed) {
    var address = (typed ?? "").Trim().Trim('"').Trim();
    if (address.Length == 0) return null;

    try {
      address = Path.GetFullPath(address);
    } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
      return null;
    }

    if (Directory.Exists(address)) return Location.Folder(address);
    if (File.Exists(address)) return IsArchive(address) ? Location.InArchive(address, "") : null;

    return ResolveIntoArchive(address);
  }

  /// <summary>Finds the archive file partway along the address and takes the rest as inside it.</summary>
  private static Location? ResolveIntoArchive(string address) {
    for (var i = 0; i < address.Length; ++i) {
      if (address[i] is not ('/' or '\\')) continue;

      var prefix = address[..i];
      if (prefix.Length == 0 || Directory.Exists(prefix)) continue;
      if (!File.Exists(prefix)) return null;

      return IsArchive(prefix) ? Location.InArchive(prefix, address[(i + 1)..]) : null;
    }

    return null;
  }

  /// <summary>True when the shell can open <paramref name="path"/> and list what is inside.</summary>
  internal static bool IsArchive(string path) {
    try {
      FormatRegistration.EnsureInitialized();
      var format = FormatDetector.Detect(path);
      return format != FormatDetector.Format.Unknown && FormatRegistry.GetArchiveOps(format.ToString()) is not null;
    } catch {
      // Detection that throws on an odd file is a "no", not an error for the address bar to report.
      return false;
    }
  }
}
