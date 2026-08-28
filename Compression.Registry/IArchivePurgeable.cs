namespace Compression.Registry;

/// <summary>
/// Opt-in capability: all user/live entries can be removed from an existing
/// container while leaving a valid, listable empty instance. This is distinct
/// from <see cref="IWipeEmpty"/>, which preserves live entries and overwrites only
/// unused/dead bytes.
/// </summary>
public interface IArchivePurgeable {
  /// <summary>
  /// Removes every live non-directory entry from <paramref name="archive"/>.
  ///
  /// <para><b>Default implementation</b>: descriptors that also implement
  /// <see cref="IArchiveFormatOperations"/> and <see cref="IArchiveModifiable"/>
  /// get a transactional staged purge through <see cref="RebuildVerb.PurgeViaModifier"/>.
  /// Native implementations may override this when they can empty the container
  /// more efficiently.</para>
  /// </summary>
  void Purge(Stream archive) {
    if (this is not IArchiveFormatOperations ops || this is not IArchiveModifiable modifier)
      throw new NotSupportedException(
        "The default Purge requires IArchiveFormatOperations + IArchiveModifiable.");
    RebuildVerb.PurgeViaModifier(archive, ops, modifier);
  }
}
