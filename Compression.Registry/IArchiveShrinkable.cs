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
  void Shrink(Stream input, Stream output) {
    if (this is not IArchiveFormatOperations ops || this is not IArchiveCreatable creator)
      throw new NotSupportedException(
        "The default Shrink requires the descriptor to also implement IArchiveFormatOperations + IArchiveCreatable.");
    // Rebuild into a buffer, then honour the shrink invariant "never grow, never
    // corrupt": emit the rebuilt image only when it round-tripped AND is actually
    // smaller; on any rebuild failure (writer limitation, lossy round-trip the
    // verifier rejected, etc.) fall back to copying the original through
    // unchanged. Shrink is thus total — it never throws or damages the source.
    using var rebuilt = new MemoryStream();
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
