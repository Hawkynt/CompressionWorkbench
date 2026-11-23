using System.Buffers.Binary;
using FileFormat.Xpk;

namespace FileFormat.XMash;

/// <summary>
/// Reads an Amiga X-Mash disk archive (".xms", magic "MSH") and rebuilds its floppy image.
/// </summary>
/// <remarks>
/// <para>After the three magic bytes the file is a run of blocks, each a type byte, two bytes and a
/// big-endian 32-bit length, followed by that many bytes of one XPK stream (<c>XPKF</c>):</para>
/// <list type="bullet">
///   <item><description><c>D</c>: track data; the two bytes are the first track (cylinder × 2 + head)
///   and the number of tracks, and the stream unpacks to that many 5,632-byte tracks.</description></item>
///   <item><description><c>B</c>: a banner text; <c>F</c>: a file description (FILE_ID.DIZ). Both bytes
///   are zero.</description></item>
/// </list>
/// <para>Tracks no block covers read back as zeros. The XPK sub-libraries are those
/// <see cref="Compression.Core.Dictionary.Xpk.XpkSubLibraries"/> decodes; other ones and
/// password-protected streams are refused.</para>
/// <para>Sourcing: the block layout is the producer's own documentation (X-Mash 1.2,
/// <c>File_Format.DOC</c>) and libxad's xMash client (The Unarchiver's XADMaster,
/// <c>libxad/clients/xMash.c</c>, LGPL-2.1-or-later); verified on archives X-Mash 1.2 wrote.</para>
/// </remarks>
public sealed class XMashReader {
  /// <summary>Bytes in one track: 11 sectors × 512 bytes.</summary>
  public const int TrackSize = 11 * 512;

  /// <summary>Tracks on a DD floppy: 80 cylinders × 2 heads.</summary>
  public const int Tracks = 160;

  private const int BlockHeader = 7;

  private readonly byte[] _data;
  private readonly List<(int Offset, int Length, int FirstTrack, int TrackCount)> _trackBlocks = [];

  /// <summary>Banner texts, in file order (decoded on first use).</summary>
  public IReadOnlyList<byte[]> Banners => this.DecodeTexts((byte)'B');

  /// <summary>File descriptions (FILE_ID.DIZ), in file order.</summary>
  public IReadOnlyList<byte[]> Descriptions => this.DecodeTexts((byte)'F');

  private readonly List<(byte Type, int Offset, int Length)> _textBlocks = [];

  /// <summary>First track any block covers.</summary>
  public int FirstTrack { get; } = Tracks;

  /// <summary>One past the last track any block covers.</summary>
  public int EndTrack { get; }

  /// <summary>Returns true when <paramref name="data"/> starts like an X-Mash archive.</summary>
  public static bool IsXMash(ReadOnlySpan<byte> data) =>
    data.Length >= 4 && data[..3].SequenceEqual("MSH"u8) && data[3] is (byte)'D' or (byte)'B' or (byte)'F';

  /// <summary>Reads the block list.</summary>
  /// <exception cref="InvalidDataException">Not an X-Mash archive, or a block is malformed.</exception>
  public XMashReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    var d = this._data;
    if (!IsXMash(d))
      throw new InvalidDataException("X-Mash: missing MSH header.");
    var pos = 3;
    while (pos < d.Length) {
      if (pos + BlockHeader > d.Length)
        throw new InvalidDataException($"X-Mash: truncated block header at {pos}.");
      var type = d[pos];
      var length = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos + 3));
      var start = pos + BlockHeader;
      if (length > (uint)(d.Length - start))
        throw new InvalidDataException($"X-Mash: block at {pos} runs past the end of the file.");
      switch (type) {
        case (byte)'D':
          int first = d[pos + 1], count = d[pos + 2];
          if (count == 0 || first + count > Tracks)
            throw new InvalidDataException($"X-Mash: block at {pos} covers tracks {first}+{count}.");
          this._trackBlocks.Add((start, (int)length, first, count));
          this.FirstTrack = Math.Min(this.FirstTrack, first);
          this.EndTrack = Math.Max(this.EndTrack, first + count);
          break;
        case (byte)'B' or (byte)'F':
          this._textBlocks.Add((type, start, (int)length));
          break;
        default:
          throw new InvalidDataException($"X-Mash: unknown block type 0x{type:X2} at {pos}.");
      }
      pos = start + (int)length;
    }
    if (this._trackBlocks.Count == 0)
      throw new InvalidDataException("X-Mash: no track data.");
  }

  /// <summary>Rebuilds the 880 KiB floppy; tracks not stored are zero.</summary>
  /// <exception cref="InvalidDataException">A stream is damaged or unpacks to the wrong size.</exception>
  /// <exception cref="NotSupportedException">An XPK sub-library is not implemented, or a stream is encrypted.</exception>
  public byte[] ExtractDisk() {
    var image = new byte[Tracks * TrackSize];
    foreach (var (offset, length, first, count) in this._trackBlocks) {
      var tracks = XpkStream.Decode(this._data.AsSpan(offset, length).ToArray());
      if (tracks.Length != count * TrackSize)
        throw new InvalidDataException($"X-Mash: tracks {first}+{count} unpack to {tracks.Length} bytes.");
      tracks.CopyTo(image, first * TrackSize);
    }
    return image;
  }

  /// <summary>Names of the XPK sub-libraries the track data uses.</summary>
  public IReadOnlyList<string> Packers =>
    this._trackBlocks.Select(b => XpkStream.ReadType(this._data.AsSpan(b.Offset, b.Length))).Distinct().ToList();

  private List<byte[]> DecodeTexts(byte type) =>
    this._textBlocks.Where(b => b.Type == type).Select(b => XpkStream.Decode(this._data.AsSpan(b.Offset, b.Length).ToArray())).ToList();
}
