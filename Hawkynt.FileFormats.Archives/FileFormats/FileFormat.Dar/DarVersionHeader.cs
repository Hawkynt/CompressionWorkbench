using System.Text;

namespace FileFormat.Dar;

/// <summary>
/// The version header written at the start of a DAR archive and repeated as the version trailer
/// before its end (<c>docs/DAR-ON-DISK.md</c>, section 4): format edition, compression letter,
/// user comment, flags, then the optional fields the flags announce and a 2-byte checksum.
/// </summary>
/// <param name="Major">Archive format major number.</param>
/// <param name="Minor">Archive format minor number.</param>
/// <param name="Algorithm">Compression letter (<c>n</c>, <c>z</c>, <c>y</c>, <c>x</c>, <c>d</c>, <c>q</c>, <c>l</c>...).</param>
/// <param name="UserComment">The user comment (dar writes <c>N/A</c> unless told otherwise).</param>
/// <param name="Flags">The flag field with continuation bits removed.</param>
/// <param name="InitialOffset">Where file data starts in the archive; 0 when not recorded.</param>
/// <param name="BlockSize">Compression block size; 0 for streaming compression.</param>
public sealed record DarVersionHeader(int Major, int Minor, char Algorithm, string UserComment, uint Flags, ulong InitialOffset, ulong BlockSize) {

  /// <summary>Width of the version header's checksum.</summary>
  public const int ChecksumWidth = 2;

  /// <summary>Whether escape marks (tape marks) are interleaved with the data.</summary>
  public bool HasTapeMarks => (this.Flags & DarArchive.FlagTapeMarks) != 0;

  /// <summary>Whether the archive format is at least <paramref name="major"/>.<paramref name="minor"/>.</summary>
  public bool AtLeast(int major, int minor) => (this.Major, this.Minor).CompareTo((major, minor)) >= 0;

  /// <summary>Parses a version header or trailer from the start of <paramref name="s"/> and checks its checksum.</summary>
  /// <exception cref="InvalidDataException">Malformed or failing its checksum.</exception>
  /// <exception cref="NotSupportedException">Carries encryption parameters this reader does not decode.</exception>
  public static DarVersionHeader Parse(ReadOnlySpan<byte> s, out int length) {
    if (s.Length < 7)
      throw new InvalidDataException("The DAR version header is truncated.");
    // Edition: two digit characters (value + '0', so 11 reads ';') holding major / 256 and
    // major % 256, the minor as a third such character, then NUL.
    int Digit(byte b) => b < 48 ? b + 208 : b - 48;
    var major = Digit(s[0]) * 256 + Digit(s[1]);
    if (major < 8)
      throw new NotSupportedException($"DAR archive format {major} predates the formats this reader decodes.");
    var minor = Digit(s[2]);
    if (s[3] != 0)
      throw new InvalidDataException("The DAR version header edition is not NUL-terminated.");
    var pos = 4;
    var algorithm = (char)s[pos++];
    var comment = DarPrimitives.ReadString(s, ref pos);
    uint flags = 0;
    byte b;
    var flagBytes = 0;
    do {
      if (pos >= s.Length)
        throw new InvalidDataException("The DAR version header ends inside its flags.");
      if (++flagBytes > 4)
        throw new InvalidDataException("The DAR version header flag field is too long.");
      b = s[pos++];
      flags = (flags << 8) | (uint)(b & 0xFE);
    } while ((b & 1) != 0);
    ulong initialOffset = 0, blockSize = 0;
    if ((flags & DarArchive.FlagInitialOffset) != 0)
      initialOffset = DarPrimitives.ReadInfinint(s, ref pos);
    if ((flags & (DarArchive.FlagScrambled | DarArchive.FlagCryptedKey | DarArchive.FlagKdf)) != 0)
      throw new NotSupportedException("The DAR archive is encrypted.");
    if ((flags & DarArchive.FlagReferenceSlicing) != 0) {
      // slice layout of the archive of reference (isolated catalogues): first size, other size,
      // first header size, other header size, all infinints, then the older-format byte.
      for (var i = 0; i < 4; ++i)
        _ = DarPrimitives.ReadInfinint(s, ref pos);
      if (pos >= s.Length)
        throw new InvalidDataException("The DAR reference slicing is truncated.");
      ++pos;
    }
    if ((flags & DarArchive.FlagCompressionBlockSize) != 0)
      blockSize = DarPrimitives.ReadInfinint(s, ref pos);
    var end = pos;
    var stored = DarPrimitives.ReadChecksum(s, ref pos);
    if (!DarPrimitives.Checksum(s[..end], stored.Length).AsSpan().SequenceEqual(stored))
      throw new InvalidDataException("The DAR version header fails its checksum.");
    length = pos;
    return new DarVersionHeader(major, minor, algorithm, comment, flags, initialOffset, blockSize);
  }

  /// <summary>Writes this header in the layout dar 2.7/2.8 write.</summary>
  public void Write(Stream output) {
    using var body = new MemoryStream();
    static byte Char(int v) => (byte)(v + 48);
    body.WriteByte(Char(this.Major / 256));
    body.WriteByte(Char(this.Major % 256));
    body.WriteByte(Char(this.Minor));
    body.WriteByte(0);
    body.WriteByte((byte)this.Algorithm);
    DarPrimitives.WriteString(body, this.UserComment);
    var flags = this.Flags;
    var width = 8;
    while ((flags >> width) != 0) {
      flags |= 1u << width;
      width += 8;
    }
    while (width > 0) {
      width -= 8;
      body.WriteByte((byte)(flags >> width));
    }
    if ((this.Flags & DarArchive.FlagInitialOffset) != 0)
      DarPrimitives.WriteInfinint(body, this.InitialOffset);
    if ((this.Flags & DarArchive.FlagCompressionBlockSize) != 0)
      DarPrimitives.WriteInfinint(body, this.BlockSize);
    var bytes = body.ToArray();
    output.Write(bytes);
    DarPrimitives.WriteChecksum(output, DarPrimitives.Checksum(bytes, ChecksumWidth));
  }

  /// <inheritdoc/>
  public override string ToString() => $"{this.Major}.{this.Minor} '{this.Algorithm}' flags=0x{this.Flags:X} comment={Encoding.UTF8.GetByteCount(this.UserComment)}B";
}
