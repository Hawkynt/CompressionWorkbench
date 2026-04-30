#pragma warning disable CS1591
namespace FileFormat.Arsc;

/// <summary>
/// Reads the uniform 8-byte ARSC chunk header (Type:UInt16 LE, HeaderSize:UInt16 LE, Size:UInt32 LE)
/// and validates the inner-/outer-size invariants. All multi-byte integers are little-endian.
/// </summary>
public readonly record struct ChunkHeader(ushort Type, ushort HeaderSize, uint Size);

public static class ChunkWalker {

  /// <summary>
  /// Reads an 8-byte chunk header from <paramref name="stream"/>. Throws
  /// <see cref="EndOfStreamException"/> on truncation, <see cref="InvalidDataException"/>
  /// when <c>HeaderSize &lt; 8</c> or <c>Size &lt; HeaderSize</c>.
  /// </summary>
  public static ChunkHeader ReadHeader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    Span<byte> buf = stackalloc byte[ArscConstants.ChunkHeaderSize];
    var read = 0;
    while (read < buf.Length) {
      var n = stream.Read(buf[read..]);
      if (n <= 0) throw new EndOfStreamException("Unexpected EOF reading ARSC chunk header.");
      read += n;
    }
    var type = (ushort)(buf[0] | (buf[1] << 8));
    var headerSize = (ushort)(buf[2] | (buf[3] << 8));
    var size = (uint)(buf[4] | (buf[5] << 8) | (buf[6] << 16) | (buf[7] << 24));
    if (headerSize < ArscConstants.ChunkHeaderSize)
      throw new InvalidDataException($"Invalid ARSC chunk: HeaderSize={headerSize} below 8-byte minimum.");
    if (size < headerSize)
      throw new InvalidDataException($"Invalid ARSC chunk: Size={size} smaller than HeaderSize={headerSize}.");
    return new ChunkHeader(type, headerSize, size);
  }

  /// <summary>
  /// Skips the remaining bytes of a chunk after its 8-byte common header has already been read.
  /// Returns false when the stream is too short to skip the requested distance.
  /// </summary>
  public static bool SkipBody(Stream stream, ChunkHeader chunk) {
    ArgumentNullException.ThrowIfNull(stream);
    var remaining = (long)chunk.Size - ArscConstants.ChunkHeaderSize;
    if (remaining <= 0) return true;
    var available = stream.Length - stream.Position;
    if (remaining > available) return false;
    stream.Position += remaining;
    return true;
  }
}
