namespace Compression.Registry;

/// <summary>
/// Opt-in capability for formats whose encoded representation can be recompressed
/// without changing the decoded payload.
/// </summary>
public interface ICompressionOptimizable {
  /// <summary>Writes the smallest representation the descriptor can produce for the same decoded payload.</summary>
  void OptimizeCompression(Stream input, Stream output) {
    if (this is not IStreamFormatOperations stream)
      throw new NotSupportedException("Compression optimization requires stream-format operations.");
    stream.CompressOptimal(input, output);
  }
}

/// <summary>
/// Opt-in capability for formats that can normalize non-semantic representation
/// details while preserving the complete logical contents.
/// </summary>
public interface IArchiveCanonicalizable {
  /// <summary>Writes the canonical representation of <paramref name="input"/>.</summary>
  void Canonicalize(Stream input, Stream output);
}

/// <summary>
/// Opt-in capability for containers that can be rebuilt without changing their
/// logical entry model. This is intentionally distinct from recompression.
/// </summary>
public interface IArchiveRepackable {
  /// <summary>Rebuilds the container into <paramref name="output"/> and verifies semantic identity.</summary>
  void Repack(Stream input, Stream output) {
    if (this is not IArchiveFormatOperations operations || this is not IArchiveCreatable creator)
      throw new NotSupportedException(
        "Archive repacking requires IArchiveFormatOperations + IArchiveCreatable.");
    RebuildVerb.RebuildToStream(input, output, operations, creator);
  }
}

/// <summary>
/// Opt-in capability for reordering directory records without moving file data
/// or changing allocation chains.
/// </summary>
public interface IFilesystemDirectoryOrderer {
  /// <summary>
  /// Sorts each directory's live entry groups by name while preserving the
  /// records belonging to one logical entry as an indivisible group.
  /// </summary>
  void SortDirectoryEntries(Stream image);
}

/// <summary>
/// Optional extension for semantic state that is not represented by
/// <see cref="ArchiveEntryInfo"/>: attributes, hard-link identity, sparse extent
/// maps, container flags, ACL-like flags, or other format-specific semantics.
/// Keys and values must be deterministic and independent of physical placement.
/// </summary>
public interface IArchiveSemanticMetadataProvider {
  /// <summary>Captures format-specific semantic metadata for a preservation comparison.</summary>
  IReadOnlyDictionary<string, string> CaptureSemanticMetadata(Stream archive);
}
