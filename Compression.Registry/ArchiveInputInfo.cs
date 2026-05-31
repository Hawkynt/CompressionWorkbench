namespace Compression.Registry;

/// <summary>
/// Describes a single input file/directory for archive creation.
/// </summary>
/// <remarks>
/// <para><b>In-memory inputs.</b> When <see cref="InMemoryContent"/> is set the
/// input's bytes are taken straight from memory and <see cref="FullPath"/> need
/// not refer to a real file. This lets callers reconfigure/convert an archive or
/// filesystem image without first extracting its entries to a temporary
/// directory — the extracted streams are fed back in directly. Descriptors read
/// content through <see cref="ReadContent"/> so the same code path serves both
/// on-disk and in-memory inputs.</para>
/// </remarks>
public sealed record ArchiveInputInfo(
  string FullPath,
  string ArchiveName,
  bool IsDirectory,
  byte[]? InMemoryContent = null
) {
  /// <summary>Creates an in-memory input whose content comes from
  /// <paramref name="content"/> rather than a file on disk.</summary>
  public static ArchiveInputInfo InMemory(string archiveName, byte[] content)
    => new(FullPath: archiveName, ArchiveName: archiveName, IsDirectory: false, InMemoryContent: content);

  /// <summary>Returns the input's bytes: the in-memory content when present,
  /// otherwise the file at <see cref="FullPath"/>. Descriptors should call this
  /// instead of <c>File.ReadAllBytes(FullPath)</c> so they transparently support
  /// in-memory (temp-free) creation and conversion.</summary>
  public byte[] ReadContent()
    => this.InMemoryContent ?? System.IO.File.ReadAllBytes(this.FullPath);
}
