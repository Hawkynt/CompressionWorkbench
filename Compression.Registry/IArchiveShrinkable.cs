namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor can rebuild an archive into a new output stream,
/// stepping down to the smallest size in <see cref="CanonicalSizes"/> that still holds
/// the current payload. For formats with continuous sizing (most filesystems),
/// <see cref="CanonicalSizes"/> returns just the current image size and shrink tight-packs
/// to exactly that. For fixed-size disk image families (C64 D64/D71/D81; PC 720K/1.44M/2.88M
/// floppies; Amiga ADF), the list walks from largest to smallest standard size.
/// </summary>
public interface IArchiveShrinkable {
  /// <summary>
  /// Canonical image sizes in bytes, ascending. A 1.44 MB PC floppy descriptor returns
  /// <c>[737280, 1474560, 2949120]</c>; a filesystem without a disc-size concept returns
  /// an empty list (the default), meaning "rebuild tight / auto-fit to content".
  /// </summary>
  IReadOnlyList<long> CanonicalSizes => [];

  /// <summary>
  /// Rebuilds <paramref name="input"/> into <paramref name="output"/>, picking the
  /// smallest <see cref="CanonicalSizes"/> entry that holds the current content.
  ///
  /// <para><b>Default implementation</b>: any descriptor that also implements
  /// <see cref="IArchiveFormatOperations"/> + <see cref="IArchiveCreatable"/> (i.e. it
  /// can round-trip its own files) gets shrink for free — a verified extract → re-create
  /// rebuild via <see cref="RebuildVerb.RebuildToStream"/> that tight-packs the payload
  /// (auto-fit) and refuses to emit a lossy result. Formats with a fixed canonical-size
  /// ladder (floppy/disk images) override this to step down standard sizes.</para>
  /// </summary>
  void Shrink(Stream input, Stream output) => this.ShrinkDefault(input, output);

  /// <summary>
  /// The default rebuild-or-copy-through shrink, exposed so a format-specific
  /// <see cref="Shrink"/> override (e.g. a genuine in-place shrinker) can fall back to
  /// it when the in-place path declines an image. Rebuilds into a buffer and emits the
  /// result only when it round-tripped AND is actually smaller; on any rebuild failure
  /// it copies the original through unchanged. Shrink is thus total — it never throws
  /// or damages the source.
  /// </summary>
  void ShrinkDefault(Stream input, Stream output) {
    if (this is not IArchiveFormatOperations ops || this is not IArchiveCreatable creator) {
      // No rebuild path available: copy through unchanged (never grow, never corrupt).
      input.Position = 0;
      output.Position = 0;
      output.SetLength(0);
      input.CopyTo(output);
      return;
    }
    // A scratch file, not a MemoryStream: the rebuilt image is a whole volume,
    // and a MemoryStream cannot hold one past 2 GB ("Stream was too long").
    using var rebuilt = RebuildVerb.CreateScratchStream();
    var useRebuilt = false;
    try {
      RebuildVerb.RebuildToStream(input, rebuilt, ops, creator);
      useRebuilt = rebuilt.Length > 0 && rebuilt.Length < input.Length;
    } catch {
      useRebuilt = false;
    }
    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    if (useRebuilt) {
      rebuilt.Position = 0;
      rebuilt.CopyTo(output);
    } else {
      input.CopyTo(output);
    }
  }
}
