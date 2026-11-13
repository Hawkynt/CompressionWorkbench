namespace Compression.Registry;

/// <summary>
/// Explicit opt-in for native archive editors whose Add/Remove mutation path
/// can operate directly on a seekable source stream under an external persistent
/// undo journal. This is distinct from CanModify: a format that uses a full
/// create-only rebuild must not claim direct in-place editing.
/// </summary>
public interface IArchiveInPlaceMountWriter {
  /// <summary>
  /// Returns true only for source profiles that the descriptor can edit
  /// directly. The mounting layer must additionally verify a writable named
  /// FileStream, create and persist an undo journal and validate the result.
  /// </summary>
  bool CanEditInPlace(Stream source);
}
