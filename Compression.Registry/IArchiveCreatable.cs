namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor can produce a fresh archive from a list of inputs (WORM).
/// Descriptors that do not implement this interface cannot be created from scratch.
/// </summary>
/// <remarks>
/// Separate from <see cref="IArchiveFormatOperations"/> so callers discover the capability at
/// the type level (<c>if (ops is IArchiveCreatable c) …</c>) instead of hitting a runtime
/// <c>NotSupportedException</c>.
/// </remarks>
public interface IArchiveCreatable {
  /// <summary>
  /// Produces a fresh archive at <paramref name="output"/> containing <paramref name="inputs"/>.
  /// Existing archive contents (if any) at <paramref name="output"/> are overwritten.
  /// </summary>
  void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options);
}
