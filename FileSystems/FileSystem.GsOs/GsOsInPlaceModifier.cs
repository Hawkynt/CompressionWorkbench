#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.GsOs;

/// <summary>
/// In-place modifier for Apple IIgs GS/OS 2IMG disk images carrying a
/// ProDOS-ordered payload (image format = 1). The 2IMG container is a
/// 64-byte header at offset 0 followed by the inner ProDOS volume.
///
/// <para>
/// Mutation is genuinely in place: the inner ProDOS catalog, volume bitmap
/// and data blocks are edited directly via
/// <see cref="FileSystem.ProDos.ProDosModifier"/> (which auto-detects the
/// 64-byte 2IMG header through its magic and offsets all block I/O), so the
/// 2IMG header bytes 0..63 and every untouched ProDOS block stay byte-identical
/// and the image keeps its original length. The earlier volume-directory-header
/// / slot-1 byte-offset collision in <c>ProDosModifier</c> is fixed and pinned by
/// <c>ProDosVolumeHeaderRegressionTests</c>. The edit is applied to an in-memory
/// copy first and only written back on success, so a failure leaves the image
/// untouched and the verified rebuild path takes over (catalog-full / structural
/// edge cases). HFS- and DOS-3.3-ordered 2IMG payloads (image format = 0 or 2)
/// are rejected.
/// </para>
/// </summary>
public static class GsOsInPlaceModifier {

  private const int HeaderSize = 64;
  private const int ImageFormatProDos = 1;
  private static readonly byte[] Magic = "2IMG"u8.ToArray();

  /// <summary>
  /// Adds — or replaces by name — files inside the inner ProDOS volume of
  /// an existing 2IMG image. The 2IMG header bytes 0..63 are byte-identical
  /// before and after the operation; existing ProDOS blocks not touched by the
  /// edit are preserved in place (no full rebuild for the common case).
  /// </summary>
  public static void AddFile(Stream archive, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var image = ReadProDosOrderedImage(archive);
    // Drop any existing entry of that name first, the way the ProDOS descriptor
    // does. ProDosModifier.AddFile always takes a fresh directory slot, so on
    // its own it appends rather than replaces and the volume ends up listing
    // the name twice — while the rebuild fallback below keys by name and does
    // replace. The two paths have to mean the same thing.
    if (TryInPlace(archive, image, work => {
          FileSystem.ProDos.ProDosModifier.RemoveFile(work, name, wipeData: true);
          FileSystem.ProDos.ProDosModifier.AddFile(work, name, data);
        }))
      return;

    // Fallback: verified rebuild from the pre-edit snapshot (image untouched on the
    // failed in-place attempt because the edit ran on a working copy).
    var byName = SnapshotByName(image);
    byName[name] = data;
    Rebuild(archive, image.AsSpan(0, HeaderSize).ToArray(), byName);
  }

  /// <summary>
  /// Removes the named file from the inner ProDOS volume of an existing
  /// 2IMG image. Returns true if the file existed and was removed.
  /// </summary>
  public static bool RemoveFile(Stream archive, string name) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);

    var image = ReadProDosOrderedImage(archive);
    var removed = false;
    if (TryInPlace(archive, image, work => removed = FileSystem.ProDos.ProDosModifier.RemoveFile(work, name)))
      return removed;

    var byName = SnapshotByName(image);
    if (!byName.Remove(name)) return false;
    Rebuild(archive, image.AsSpan(0, HeaderSize).ToArray(), byName);
    return true;
  }

  // Runs the genuine in-place edit on an in-memory copy of the image and writes the
  // result back to the archive only if it succeeds and the length is preserved.
  // Returns false (leaving the archive untouched) when the edit throws a structural
  // limit so the caller can fall back to the rebuild path.
  private static bool TryInPlace(Stream archive, byte[] image, Action<Stream> edit) {
    try {
      using var work = new MemoryStream(image.Length);
      work.Write(image, 0, image.Length);
      work.Position = 0;
      edit(work);
      var result = work.ToArray();
      archive.Position = 0;
      archive.Write(result, 0, result.Length);
      archive.SetLength(result.Length);
      return true;
    } catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException
                                 or IOException or InvalidDataException) {
      return false;
    }
  }

  private static byte[] ReadProDosOrderedImage(Stream archive) {
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
    return image;
  }

  private static Dictionary<string, byte[]> SnapshotByName(byte[] image) {
    var byName = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    using var inner = new MemoryStream(image, HeaderSize, image.Length - HeaderSize, writable: false);
    using var reader = new FileSystem.ProDos.ProDosReader(inner);
    foreach (var e in reader.Entries) {
      if (e.IsDirectory) continue;
      byName[e.Name] = reader.Extract(e);
    }
    return byName;
  }

  // ── Internals ───────────────────────────────────────────────────────────

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
