namespace Compression.Registry;

/// <summary>
/// Optional capability for container-level properties that a rebuild must
/// preserve but that do not belong to one logical entry.
/// </summary>
public interface IArchiveSemanticMetadataProvider {
  /// <summary>
  /// Returns stable semantic flags for <paramref name="archive"/> such as
  /// encryption/header modes or other container settings that must survive a
  /// maintenance rebuild unchanged unless the operation explicitly changes them.
  /// </summary>
  IReadOnlyDictionary<string, string> GetContainerSemanticFlags(Stream archive, string? password);
}
