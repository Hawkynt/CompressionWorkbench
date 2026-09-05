namespace Compression.Registry;

/// <summary>
/// Opt-in capability for scattering a volume's allocation blocks across the
/// whole data area — fragmentation on purpose, so a defragmenter has something
/// real to work against.
/// </summary>
/// <remarks>
/// <para>This is deliberately not a <see cref="DefragMode" />. A caller asking
/// <see cref="IArchiveDefragmentable.Defragment(Stream, DefragOptions)" /> to
/// defragment a volume must never be able to end up scattering it because a
/// mode was mis-set, defaulted, round-tripped through a settings file, or
/// picked up from a list of every value the enum has. Reaching this needs the
/// caller to name the verb, and the descriptor to say it can do it.</para>
///
/// <para>There is no default implementation, and there is no rebuild fallback.
/// A rebuild lays a volume out contiguously, which is the exact opposite of
/// what was asked for; a descriptor that cannot scatter in place refuses
/// instead, naming what stopped it.</para>
/// </remarks>
public interface IFilesystemScrambleable {

  /// <summary>
  /// Deals every allocation block of every live owner a fresh slot from the
  /// volume's data area, at random from <see cref="ScrambleOptions.Seed" />,
  /// and moves the blocks there. Content is preserved exactly; only where it
  /// lives changes.
  /// </summary>
  /// <exception cref="InvalidOperationException">The volume's layout cannot be
  /// scattered in place — the mover cannot relink a scattered owner, or the
  /// shuffle needs somewhere to hold a block and the volume has nowhere.</exception>
  void Scramble(Stream image, ScrambleOptions options);
}
