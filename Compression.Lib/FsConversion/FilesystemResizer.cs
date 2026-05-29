#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Lib.FsConversion;

/// <summary>
/// Top-level dispatcher for in-place filesystem shrink / grow operations.
/// Routes by <c>fsId</c> (the format-registry name — e.g. "Fat", "Ext",
/// "Ext1") to a format-specific resizer that mutates the supplied
/// <see cref="Stream"/> in place.
///
/// <para>This is the primitive that <see cref="Compression.Core.DiskImage.PartitionEditor.ResizePartition"/>
/// uses to resize the contents of a partition before updating the
/// partition table. It is also usable standalone on a single-filesystem
/// image stream.</para>
///
/// <para><b>Supported FS pairs:</b>
/// <list type="bullet">
///   <item><c>Fat</c> — FAT12/16/32 shrink + grow via <see cref="FatResizer"/>.</item>
///   <item><c>Ext</c>, <c>Ext1</c> — ext2/3/4 single-group shrink + grow via
///     <see cref="ExtResizer"/>.</item>
/// </list>
/// Other filesystems either lack the write-side block-mover infrastructure
/// or are inherently WORM/read-only — for those, the caller should use the
/// extract-and-rebuild path in <see cref="ArchiveOperations.Resize"/> instead.</para>
///
/// <para><b>Crash safety:</b> each step uses targeted writes + flush
/// barriers — see the per-format resizers' remarks for details. A crash
/// mid-resize leaves the FS readable at one of the two endpoint sizes;
/// fsck will report orphaned blocks (recoverable, no data loss).</para>
/// </summary>
public static class FilesystemResizer {

  /// <summary>
  /// Shrinks the in-stream filesystem to <paramref name="newSizeBytes"/>.
  /// </summary>
  /// <param name="image">Readable, writable, seekable image stream. Will be
  /// truncated on success.</param>
  /// <param name="fsId">Format-registry ID (e.g. "Fat", "Ext", "Ext1").</param>
  /// <param name="newSizeBytes">Target size in bytes. Must be smaller than
  /// the current stream length and large enough to hold all live content.</param>
  /// <exception cref="ArgumentNullException"><paramref name="image"/> or
  /// <paramref name="fsId"/> is null.</exception>
  /// <exception cref="NotSupportedException">The given <paramref name="fsId"/>
  /// is not one of the supported pairs.</exception>
  /// <exception cref="InvalidOperationException">Live content does not fit in
  /// the requested target size, or the FS is structurally unable to be
  /// shrunk that small (e.g. metadata blocks would be lost).</exception>
  public static void Shrink(Stream image, string fsId, long newSizeBytes) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentException.ThrowIfNullOrEmpty(fsId);
    if (newSizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(newSizeBytes));

    switch (NormaliseFsId(fsId)) {
      case "Fat":
        FatResizer.Shrink(image, newSizeBytes);
        return;
      case "Ext":
      case "Ext1":
        ExtResizer.Shrink(image, newSizeBytes);
        return;
      default:
        throw new NotSupportedException(
          $"Filesystem '{fsId}' does not support in-place shrink. " +
          "Supported: Fat, Ext, Ext1. Use ArchiveOperations.Resize for extract-then-rebuild fallback.");
    }
  }

  /// <summary>
  /// Grows the in-stream filesystem to <paramref name="newSizeBytes"/>.
  /// </summary>
  /// <param name="image">Readable, writable, seekable image stream. Will be
  /// extended via <see cref="Stream.SetLength"/>.</param>
  /// <param name="fsId">Format-registry ID (e.g. "Fat", "Ext", "Ext1").</param>
  /// <param name="newSizeBytes">Target size in bytes. Must be greater than
  /// the current stream length.</param>
  /// <exception cref="NotSupportedException">Either the FS is unsupported
  /// for in-place grow, or the requested grow would cross a structural
  /// threshold (FAT type change, additional ext block group, etc.).</exception>
  public static void Grow(Stream image, string fsId, long newSizeBytes) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentException.ThrowIfNullOrEmpty(fsId);
    if (newSizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(newSizeBytes));

    switch (NormaliseFsId(fsId)) {
      case "Fat":
        FatResizer.Grow(image, newSizeBytes);
        return;
      case "Ext":
      case "Ext1":
        ExtResizer.Grow(image, newSizeBytes);
        return;
      default:
        throw new NotSupportedException(
          $"Filesystem '{fsId}' does not support in-place grow. " +
          "Supported: Fat, Ext, Ext1.");
    }
  }

  /// <summary>
  /// Returns true iff <paramref name="fsId"/> can be resized in place by
  /// either <see cref="Shrink"/> or <see cref="Grow"/>.
  /// </summary>
  public static bool IsSupported(string fsId) {
    if (string.IsNullOrEmpty(fsId)) return false;
    return NormaliseFsId(fsId) is "Fat" or "Ext" or "Ext1";
  }

  /// <summary>
  /// Detects the FS at the head of <paramref name="image"/> using the
  /// format registry's magic-byte detector. Returns the descriptor ID
  /// (e.g. "Fat"), or null if no FS is recognised.
  /// </summary>
  public static string? Detect(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    FormatRegistration.EnsureInitialized();
    var savedPos = image.Position;
    try {
      image.Position = 0;
      var header = new byte[Math.Min(4096, (int)image.Length)];
      var read = image.Read(header, 0, header.Length);
      var fmt = FormatDetector.DetectByMagic(header.AsSpan(0, read));
      return fmt == FormatDetector.Format.Unknown ? null : fmt.ToString();
    } finally {
      image.Position = savedPos;
    }
  }

  // Format-registry IDs are case-sensitive but users may pass e.g.
  // "fat32" or "ext4" — collapse common aliases.
  private static string NormaliseFsId(string fsId) =>
    fsId.Trim().ToLowerInvariant() switch {
      "fat" or "fat12" or "fat16" or "fat32" => "Fat",
      "ext" or "ext2" or "ext3" or "ext4" => "Ext",
      "ext1" => "Ext1",
      _ => fsId,
    };
}
