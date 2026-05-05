namespace FileFormat.Psf;

/// <summary>
/// A synthetic entry exposed by <see cref="PsfReader"/> for the flat-archive view of a PSF.
/// PSFs aren't true archives — these entries surface the container's logical components
/// (header, reserved blob, decompressed program, parsed tags) so the standard archive
/// browse/extract UX works against them.
/// </summary>
public sealed class PsfEntry {
  /// <summary>Synthetic entry name (e.g. <c>header.bin</c>, <c>program.bin</c>, <c>tags.txt</c>).</summary>
  public string Name { get; init; } = "";

  /// <summary>Raw bytes of the entry. For <c>program.bin</c> these are post-zlib decompression.</summary>
  public byte[] Data { get; init; } = [];
}
