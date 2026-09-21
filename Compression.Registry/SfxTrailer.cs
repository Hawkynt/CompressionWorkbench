namespace Compression.Registry;

/// <summary>
/// The twelve bytes a self-extracting archive carries at its very end, locating the payload inside
/// the executable: <c>[int64 little-endian archive offset][ "SFX!" ]</c>.
/// </summary>
/// <remarks>
/// <para>
/// This lives here, rather than beside the builder that writes it, because the stub that reads it
/// must not reference <c>Compression.Lib</c>: the generated <c>RegisterFormats()</c> there
/// constructs every descriptor in the reference closure, so a single reference roots the entire
/// format library and nothing can be trimmed away. <c>Compression.Registry</c> is the largest
/// assembly a carved stub can afford — it links down to a few kilobytes.
/// </para>
/// <para>
/// The format is deliberately not versioned and deliberately does not record which format the
/// payload is. It is re-sniffed from the payload bytes on every read, so a stub that cannot
/// identify a container says so plainly instead of trusting a stale label.
/// </para>
/// </remarks>
public static class SfxTrailer {
  /// <summary>Total size of the trailer in bytes.</summary>
  public const int Size = 12;

  /// <summary>The four bytes that mark a file as one of ours.</summary>
  public static ReadOnlySpan<byte> Magic => "SFX!"u8;

  /// <summary>Where the archive payload sits inside the executable.</summary>
  /// <param name="Offset">Byte offset of the first payload byte, which is also the stub's length.</param>
  /// <param name="Length">Payload length in bytes, excluding the trailer.</param>
  public readonly record struct Location(long Offset, long Length);

  /// <summary>
  /// Reads the trailer from the end of <paramref name="stream"/>. Returns false for anything that is
  /// not one of ours, including a file too short to hold a trailer, a bad magic, or an offset that
  /// does not describe a payload inside the file.
  /// </summary>
  /// <param name="stream">A seekable stream over the whole self-extracting file.</param>
  /// <param name="location">Where the payload is, when this returns true.</param>
  public static bool TryRead(Stream stream, out Location location) {
    location = default;
    if (stream.Length < Size) return false;

    stream.Seek(-Size, SeekOrigin.End);
    Span<byte> trailer = stackalloc byte[Size];
    stream.ReadExactly(trailer);

    if (!trailer[8..].SequenceEqual(Magic)) return false;

    var offset = BitConverter.ToInt64(trailer[..8]);
    var length = stream.Length - Size - offset;

    // A payload has to start inside the file and be non-empty. Both guards matter: a truncated
    // download and a file that is nothing but a trailer both land here.
    if (offset < 0 || offset >= stream.Length - Size || length <= 0) return false;

    location = new(offset, length);
    return true;
  }

  /// <summary>
  /// Appends the trailer, recording that the payload began at <paramref name="archiveOffset"/>.
  /// </summary>
  /// <param name="stream">The output stream, positioned at the end of the payload.</param>
  /// <param name="archiveOffset">Byte offset where the payload started.</param>
  public static void Write(Stream stream, long archiveOffset) {
    Span<byte> trailer = stackalloc byte[Size];
    BitConverter.TryWriteBytes(trailer[..8], archiveOffset);
    Magic.CopyTo(trailer[8..]);
    stream.Write(trailer);
  }
}
