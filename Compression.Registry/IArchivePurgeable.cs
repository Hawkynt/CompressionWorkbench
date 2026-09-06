namespace Compression.Registry;

/// <summary>
/// Opt-in capability: all user/live entries can be removed from an existing
/// container while leaving a valid, listable empty instance. This is distinct
/// from <see cref="IWipeEmpty"/>, which preserves live entries and overwrites only
/// unused/dead bytes.
/// </summary>
public interface IArchivePurgeable {
  /// <summary>
  /// False when the format has no empty instance for a purge to leave behind: the
  /// container mandates at least one member, so removing every entry cannot end in
  /// a valid container. Individual entries still come and go through
  /// <see cref="IArchiveModifiable"/> — it is only the empty end state that does not
  /// exist — so a caller offering the verb should not offer it for these, and
  /// <see cref="Purge"/> says so rather than leaving a container its own reader
  /// rejects.
  /// </summary>
  bool CanPurgeToEmpty => true;

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
    if (!this.CanPurgeToEmpty)
      throw new NotSupportedException(
        "This format has no empty instance: the container mandates at least one member, "
        + "so there is no valid result for a purge to leave behind.");
    if (this is not IArchiveFormatOperations ops || this is not IArchiveModifiable modifier)
      throw new NotSupportedException(
        "The default Purge requires IArchiveFormatOperations + IArchiveModifiable.");
    RebuildVerb.PurgeViaModifier(archive, ops, modifier);
  }
}
