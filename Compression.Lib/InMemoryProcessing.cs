using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// Routes archive / filesystem-image reconfiguration, defragmentation and
/// conversion through an all-in-memory pipeline when the image is below a
/// configurable size threshold, falling back to the streaming / temp-file path
/// for larger images.
/// </summary>
/// <remarks>
/// <para>Small images (the common case) are read, rebuilt and written back
/// entirely in RAM: extracted entries are fed back as
/// <see cref="ArchiveInputInfo.InMemory"/> inputs, so a conversion never has to
/// spill the contents to a temporary directory on disk. The rebuilt bytes are
/// committed with <see cref="AtomicFileWriter.WriteAllBytesAtomic"/>
/// (temp-file + rename) so a crash mid-write never corrupts the target.</para>
/// <para>The threshold is configurable so tests can exercise the in-memory path
/// with tiny images (e.g. 128 KiB) while production keeps a large ceiling
/// (default 2 GiB) before falling back to disk-backed processing.</para>
/// </remarks>
public static class InMemoryProcessing {

  /// <summary>Default in-memory ceiling for production: images up to this many
  /// bytes are reconfigured/converted entirely in RAM.</summary>
  public const long DefaultThresholdBytes = 2L * 1024 * 1024 * 1024; // 2 GiB

  /// <summary>Images at or below this many bytes are processed fully in memory;
  /// larger images use the disk-backed streaming path. Configurable so tests can
  /// drive the in-memory path with small values and deployments can raise it.</summary>
  public static long ThresholdBytes { get; set; } = DefaultThresholdBytes;

  /// <summary>True when an image of <paramref name="sizeBytes"/> should be
  /// processed entirely in memory under the current <see cref="ThresholdBytes"/>.</summary>
  public static bool FitsInMemory(long sizeBytes) => sizeBytes >= 0 && sizeBytes <= ThresholdBytes;

  /// <summary>
  /// Builds an archive/image entirely in memory from the given inputs and
  /// returns the bytes. The inputs may be on-disk or
  /// <see cref="ArchiveInputInfo.InMemory">in-memory</see>; descriptors read
  /// them through <see cref="ArchiveInputInfo.ReadContent"/>, so no temporary
  /// extraction directory is involved.
  /// </summary>
  public static byte[] BuildInMemory(
      IArchiveCreatable creatable, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(creatable);
    using var ms = new MemoryStream();
    creatable.Create(ms, inputs, options);
    return ms.ToArray();
  }

  /// <summary>
  /// Reconfigures/converts an archive in memory: builds a fresh image from the
  /// already-extracted in-memory <paramref name="inputs"/> via the target
  /// <paramref name="creatable"/>, then commits it to <paramref name="targetPath"/>
  /// atomically (temp-file + rename). No temporary extraction directory is used.
  /// </summary>
  public static void RebuildToFileAtomic(
      string targetPath, IArchiveCreatable creatable,
      IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var bytes = BuildInMemory(creatable, inputs, options);
    AtomicFileWriter.WriteAllBytesAtomic(targetPath, bytes);
  }
}
