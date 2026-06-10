#pragma warning disable CS1591
using FileFormat.Zip;

namespace FileFormat.Ipsw;

/// <summary>
/// In-place modifier for Apple IPSW packages. An IPSW is just a ZIP file with
/// an Apple-specific entry layout, so every mutation routes straight through
/// <see cref="ZipModifier"/> — the central directory and EOCD record are the
/// only structural bytes the operation touches.
/// </summary>
/// <remarks>
/// <para>The name space here is the raw ZIP entry namespace (the same
/// <c>BuildManifest.plist</c>, <c>Firmware/…</c>, <c>*.dmg</c> paths the
/// underlying archive uses). The canonical name lift the descriptor performs
/// on read (<c>Firmware/iBSS.…</c> trimmed to just the filename, etc.) is a
/// listing convenience — write operations expect real ZIP paths.</para>
/// <para><b>Honest scope</b>: only the ZIP container is mutated. Inner DMG
/// or firmware blob mutation is delegated to <c>FileFormat.Dmg</c> and the
/// per-firmware descriptors; we don't decompress/repack those payloads.</para>
/// </remarks>
public static class IpswInPlaceModifier {

  /// <summary>
  /// Adds (or replaces by ZIP path) a single entry inside the IPSW. The
  /// previous entry's bytes are wiped via <see cref="ZipModifier.RemoveFile"/>
  /// before the new entry is appended.
  /// </summary>
  public static void AddEntry(Stream ipsw, string zipPath, byte[] data) {
    ArgumentNullException.ThrowIfNull(ipsw);
    ArgumentNullException.ThrowIfNull(zipPath);
    ArgumentNullException.ThrowIfNull(data);
    ZipModifier.RemoveFile(ipsw, zipPath, wipeData: true);
    ZipModifier.AddFile(ipsw, zipPath, data);
  }

  /// <summary>
  /// Removes a single entry by ZIP path. Returns true if removed.
  /// </summary>
  public static bool RemoveEntry(Stream ipsw, string zipPath) {
    ArgumentNullException.ThrowIfNull(ipsw);
    ArgumentNullException.ThrowIfNull(zipPath);
    return ZipModifier.RemoveFile(ipsw, zipPath, wipeData: true);
  }
}
