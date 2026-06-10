#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.GsOs;

/// <summary>
/// In-place modifier for Apple IIgs GS/OS 2IMG disk images carrying a
/// ProDOS-ordered payload (image format = 1). The 2IMG container is a
/// 64-byte header at offset 0 followed by the inner ProDOS volume.
///
/// <para>
/// Mutation rebuilds the inner ProDOS payload from its existing file list
/// plus the new one via <see cref="FileSystem.ProDos.ProDosWriter"/>, then
/// re-wraps it with a fresh 2IMG header. The 2IMG header bytes 0..63 stay
/// byte-identical across operations (creator code, image format, flags,
/// data offset and length). HFS- and DOS-3.3-ordered 2IMG payloads
/// (image format = 0 or 2) are rejected.
/// </para>
/// <para>
/// Note: an earlier draft delegated directly to
/// <see cref="FileSystem.ProDos.ProDosModifier"/>, which has a pre-existing
/// volume-directory-header / slot-1 byte-offset collision (the volume
/// header uses 43 bytes but EntrySize is 39) that corrupts the bitmap
/// pointer and total-blocks fields after the first file is added. The
/// rebuild path avoids that latent bug by sending the data through the
/// fresh-build code path which already round-trips correctly through the
/// reader.
/// </para>
/// </summary>
public static class GsOsInPlaceModifier {

  private const int HeaderSize = 64;
  private const int ImageFormatProDos = 1;
  private static readonly byte[] Magic = "2IMG"u8.ToArray();

  /// <summary>
  /// Adds — or replaces by name — files inside the inner ProDOS volume of
  /// an existing 2IMG image. The 2IMG header bytes 0..63 are byte-identical
  /// before and after the operation.
  /// </summary>
  public static void AddFile(Stream archive, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var (originalHeader, files) = SnapshotProDosFiles(archive);
    var byName = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var (n, d) in files) byName[n] = d;
    byName[name] = data;
    Rebuild(archive, originalHeader, byName);
  }

  /// <summary>
  /// Removes the named file from the inner ProDOS volume of an existing
  /// 2IMG image. Returns true if the file existed and was removed.
  /// </summary>
  public static bool RemoveFile(Stream archive, string name) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);

    var (originalHeader, files) = SnapshotProDosFiles(archive);
    var byName = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var (n, d) in files) byName[n] = d;
    if (!byName.Remove(name)) return false;
    Rebuild(archive, originalHeader, byName);
    return true;
  }

  // ── Internals ───────────────────────────────────────────────────────────

  private static (byte[] Header, List<(string Name, byte[] Data)> Files) SnapshotProDosFiles(Stream archive) {
    if (archive.Length < HeaderSize)
      throw new InvalidDataException("GS/OS: stream too small for a 2IMG header.");

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();

    if (!image.AsSpan(0, 4).SequenceEqual(Magic))
      throw new InvalidDataException("GS/OS: missing 2IMG magic.");
    var imageFormat = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(12, 4));
    if (imageFormat != ImageFormatProDos)
      throw new NotSupportedException(
        $"GS/OS: in-place modify currently supports only ProDOS-ordered payloads (image_format=1); got {imageFormat}.");

    var header = new byte[HeaderSize];
    Buffer.BlockCopy(image, 0, header, 0, HeaderSize);

    var files = new List<(string Name, byte[] Data)>();
    using (var inner = new MemoryStream(image, HeaderSize, image.Length - HeaderSize, writable: false)) {
      using var reader = new FileSystem.ProDos.ProDosReader(inner);
      foreach (var e in reader.Entries) {
        if (e.IsDirectory) continue;
        files.Add((e.Name, reader.Extract(e)));
      }
    }
    return (header, files);
  }

  private static void Rebuild(Stream archive, byte[] originalHeader,
                              Dictionary<string, byte[]> byName) {
    // Compute the inner ProDOS volume size from the original header so the
    // rebuilt image keeps the same total-blocks footprint (140 KB or 800 KB).
    var dataBlockCount = BinaryPrimitives.ReadUInt32LittleEndian(originalHeader.AsSpan(20, 4));
    var totalBlocks = (int)dataBlockCount;
    if (totalBlocks != FileSystem.ProDos.ProDosWriter.FloppyTotalBlocks
        && totalBlocks != FileSystem.ProDos.ProDosWriter.Disk800KTotalBlocks)
      totalBlocks = FileSystem.ProDos.ProDosWriter.FloppyTotalBlocks;

    var w = new FileSystem.ProDos.ProDosWriter();
    foreach (var (n, d) in byName)
      w.AddFile(n, d);
    var prodos = w.Build(totalBlocks: totalBlocks);

    // Preserve the original 2IMG header bytes (creator code, flags,
    // comment offsets, etc.) instead of writing a fresh one — they identify
    // the image to GS/OS-aware emulators.
    archive.Position = 0;
    archive.Write(originalHeader);
    archive.Write(prodos);
    archive.SetLength(HeaderSize + prodos.Length);
  }
}
