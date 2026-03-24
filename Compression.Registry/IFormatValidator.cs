namespace Compression.Registry;

/// <summary>
/// Optional interface for format descriptors that can perform deep validation
/// beyond simple magic byte matching. Implementations progressively validate
/// header fields, structural coherence, and data integrity.
/// </summary>
public interface IFormatValidator {
  /// <summary>
  /// Validate header fields beyond magic bytes: version numbers, flags, field ranges,
  /// plausible sizes. Requires only the first few hundred bytes.
  /// </summary>
  /// <param name="header">File data (at least the header region).</param>
  /// <param name="fileSize">Total file size for plausibility checks.</param>
  ValidationResult ValidateHeader(ReadOnlySpan<byte> header, long fileSize);

  /// <summary>
  /// Parse the directory/TOC and verify structural coherence: entry counts match,
  /// offsets are within bounds, no overlapping entries. Requires seekable stream.
  /// </summary>
  ValidationResult ValidateStructure(Stream stream);

  /// <summary>
  /// Verify checksums and/or attempt partial decompression. Most expensive level.
  /// </summary>
  ValidationResult ValidateIntegrity(Stream stream);
}
