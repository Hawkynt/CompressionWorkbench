namespace Compression.Registry;

/// <summary>
/// Opt-in capability for rebuilding a container from the same logical entries.
/// Repacking may change physical placement, headers and packing boundaries, but
/// is not by itself compression optimization.
/// </summary>
public interface IArchiveRepackable {
  /// <summary>
  /// Rebuilds <paramref name="input"/> into <paramref name="output"/> while
  /// preserving the logical entry set and contents.
  /// </summary>
  void Repack(Stream input, Stream output)
    => Repack(input, output, password: null);

  /// <summary>
  /// Password-aware repack. The same password is used to read the source and to
  /// protect the rebuilt target. Formats with different source/target password
  /// semantics can override this overload.
  /// </summary>
  void Repack(Stream input, Stream output, string? password) {
    if (this is not IArchiveFormatOperations ops || this is not IArchiveCreatable creator)
      throw new NotSupportedException(
        "The default Repack requires IArchiveFormatOperations + IArchiveCreatable.");

    RebuildVerb.RebuildToStream(
      input,
      output,
      ops,
      creator,
      createOptions: new FormatCreateOptions { Password = password },
      password: password);
  }
}
