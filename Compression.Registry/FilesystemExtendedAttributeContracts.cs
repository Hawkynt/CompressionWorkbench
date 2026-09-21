#pragma warning disable CS1591

namespace Compression.Registry;

/// <summary>
/// Optional mounted-filesystem capability for reading native extended attributes by
/// stable node identity. Distributed filesystems such as BeeGFS and GlusterFS store
/// part of their own metadata in xattrs on a backing filesystem; exposing xattrs at
/// the session boundary keeps those outer drivers independent of ext/XFS internals.
/// </summary>
public interface IFilesystemExtendedAttributeReader {
  /// <summary>
  /// Reads all extended attributes attached to <paramref name="nodeId"/>.
  /// Returned keys use the backing filesystem's normalized namespace-qualified names
  /// (for example <c>user.fhgfs</c> or <c>trusted.gfid</c>).
  /// </summary>
  IReadOnlyDictionary<string, byte[]> ReadExtendedAttributes(FilesystemNodeId nodeId);
}
