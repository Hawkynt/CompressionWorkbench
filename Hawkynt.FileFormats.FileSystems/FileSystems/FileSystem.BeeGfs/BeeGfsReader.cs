#pragma warning disable CS1591

namespace FileSystem.BeeGfs;

/// <summary>
/// Legacy compatibility surface for the former synthetic single-stream BeeGFS reader.
/// </summary>
/// <remarks>
/// BeeGFS does not define a standalone byte-stream image or an offset-zero <c>BeeGFS</c>/<c>BeeG</c>
/// header. Metadata and storage targets are directories on ordinary local filesystems and a
/// logical namespace can span multiple targets. This type is retained only to avoid deleting an
/// existing public type; construction now fails closed rather than interpreting arbitrary tagged
/// bytes as BeeGFS media.
/// </remarks>
[Obsolete("BeeGFS has no standalone single-stream image. Use BeeGfsFormatDescriptor readiness information until a directory-/multi-target snapshot API is available.")]
public sealed class BeeGfsReader : IDisposable {

  /// <summary>Legacy synthetic long tag retained for source compatibility; it is not a BeeGFS on-disk signature.</summary>
  [Obsolete("This was a synthetic CompressionWorkbench tag, not a BeeGFS on-disk signature.")]
  public static readonly byte[] LongTag = "BeeGFS"u8.ToArray();

  /// <summary>Legacy synthetic short tag retained for source compatibility; it is not a BeeGFS on-disk signature.</summary>
  [Obsolete("This was a synthetic CompressionWorkbench tag, not a BeeGFS on-disk signature.")]
  public static readonly byte[] ShortTag = "BeeG"u8.ToArray();

  /// <summary>Gets the legacy entry collection. A reader instance cannot currently be opened.</summary>
  public IReadOnlyList<BeeGfsEntry> Entries => [];

  /// <summary>Gets the legacy tag value. A reader instance cannot currently be opened.</summary>
  public string Tag => string.Empty;

  /// <summary>Gets the legacy trailing word. A reader instance cannot currently be opened.</summary>
  public uint TrailingWord => 0;

  /// <summary>Gets whether a legacy synthetic header was accepted. It is always false.</summary>
  public bool ValidHeader => false;

  /// <summary>
  /// Initializes a compatibility reader and rejects the unsupported single-stream model.
  /// </summary>
  public BeeGfsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    throw new NotSupportedException(
      "BeeGFS has no standalone single-stream image or 'BeeGFS'/'BeeG' offset-zero magic. " +
      "Open the backing ext4/XFS target filesystem separately; reconstructing a BeeGFS namespace " +
      "requires metadata/storage target directories plus target and stripe mappings.");
  }

  /// <summary>
  /// Rejects extraction through the retired synthetic reader.
  /// </summary>
  public byte[] Extract(BeeGfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    throw new NotSupportedException("Extraction through the synthetic BeeGFS stream reader is not supported.");
  }

  /// <summary>Releases resources held by this compatibility surface.</summary>
  public void Dispose() { }
}
