using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Zap;

/// <summary>
/// Reads an Amiga ZAP 1.41 disk archive and rebuilds its floppy image.
/// </summary>
/// <remarks>
/// <para>Layout (big-endian): the length of the identification text and the text itself
/// (<c>ZAP V1.41 …</c>); the length of the attached banner and the banner block (0 = none); then ten
/// blocks of eight cylinders (90,112 bytes), each a length and either the raw block (length 90,112) or
/// an LZ77-crunched, run-length-encoded block. See <c>docs/ZAP-ON-DISK.md</c>.</para>
/// <para>The cruncher reads its input backwards: the last long word is the unpacked length, the one
/// before it a checksum that the XOR of every further long word cancels; bits are taken
/// least-significant first from long words read towards the start, and the output is written
/// from its end towards its start.</para>
/// <para>Sourcing: converted from libxad's ZAP client (Stuart Caie, <c>libxad/amiga/source/clients/ZAP.c</c>,
/// LGPL-2.1-or-later); verified on archives ZAP 1.41 wrote, which rebuild their source floppy byte for
/// byte.</para>
/// </remarks>
public sealed class ZapReader {
  /// <summary>Bytes in one cylinder: 2 tracks × 11 sectors × 512 bytes.</summary>
  public const int CylinderSize = 22 * 512;

  /// <summary>Cylinders on a DD floppy.</summary>
  public const int Cylinders = 80;

  /// <summary>Bytes in one block of eight cylinders.</summary>
  public const int BlockSize = 8 * CylinderSize;

  /// <summary>Blocks in an archive.</summary>
  public const int Blocks = Cylinders / 8;

  private readonly byte[] _data;
  private readonly (int Offset, int Size)[] _blocks = new (int, int)[Blocks];
  private readonly int _bannerOffset;
  private readonly int _bannerSize;

  /// <summary>The identification text at the start of the file.</summary>
  public string Identification { get; }

  /// <summary>Returns true when <paramref name="data"/> carries the ZAP 1.41 identification.</summary>
  public static bool IsZap(ReadOnlySpan<byte> data) => data.Length >= 13 && data.Slice(4, 9).SequenceEqual("ZAP V1.41"u8);

  /// <summary>Reads the header, banner length and block table.</summary>
  /// <exception cref="InvalidDataException">Not a ZAP archive, or a length points outside the file.</exception>
  public ZapReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    var d = this._data;
    if (!IsZap(d))
      throw new InvalidDataException("ZAP: missing ZAP V1.41 identification.");
    var pos = 0;
    var idLength = this.ReadLength(ref pos, "identification");
    this.Identification = Encoding.Latin1.GetString(d, pos, idLength).TrimEnd('\0');
    pos += idLength;
    this._bannerSize = this.ReadLength(ref pos, "banner");
    this._bannerOffset = pos;
    pos += this._bannerSize;
    for (var b = 0; b < Blocks; ++b) {
      var size = this.ReadLength(ref pos, $"block {b}");
      if (size > BlockSize || size < 8)
        throw new InvalidDataException($"ZAP: block {b} has impossible length {size}.");
      this._blocks[b] = (pos, size);
      pos += size;
    }
  }

  private int ReadLength(ref int pos, string what) {
    if (pos + 4 > this._data.Length)
      throw new InvalidDataException($"ZAP: truncated before the {what} length.");
    var length = BinaryPrimitives.ReadUInt32BigEndian(this._data.AsSpan(pos));
    pos += 4;
    if (length > (uint)(this._data.Length - pos))
      throw new InvalidDataException($"ZAP: {what} runs past the end of the file.");
    return (int)length;
  }

  /// <summary>Number of blocks stored raw (the cruncher could not shrink them).</summary>
  public int StoredBlocks => this._blocks.Count(b => b.Size == BlockSize);

  /// <summary>The attached banner text, or <see langword="null"/> when the archive has none.</summary>
  /// <exception cref="InvalidDataException">The banner block is damaged.</exception>
  public byte[]? Banner {
    get {
      if (this._bannerSize == 0)
        return null;
      var block = this._data.AsSpan(this._bannerOffset, this._bannerSize);
      if (block.Length < 8)
        throw new InvalidDataException("ZAP: banner block is too short.");
      var length = BinaryPrimitives.ReadUInt32BigEndian(block[^4..]);
      if (length > 1 << 20)
        throw new InvalidDataException("ZAP: banner is implausibly long.");
      var text = new byte[length];
      Decrunch(block, text);
      return text;
    }
  }

  /// <summary>Rebuilds the 880 KiB floppy.</summary>
  /// <exception cref="InvalidDataException">A block is damaged, fails its checksum or unpacks to the wrong size.</exception>
  public byte[] ExtractDisk() {
    var image = new byte[Cylinders * CylinderSize];
    var temp = new byte[BlockSize];
    for (var b = 0; b < Blocks; ++b) {
      var (offset, size) = this._blocks[b];
      var block = this._data.AsSpan(offset, size);
      var target = image.AsSpan(b * BlockSize, BlockSize);
      if (size == BlockSize) {
        block.CopyTo(target);
        continue;
      }
      var length = BinaryPrimitives.ReadUInt32BigEndian(block[^4..]);
      if (length > BlockSize)
        throw new InvalidDataException($"ZAP: block {b} claims {length} crunched bytes.");
      Decrunch(block, temp.AsSpan(0, (int)length));
      RunLengthDecode(temp.AsSpan(0, (int)length), target, b);
    }
    return image;
  }

  /// <summary>Undoes the LZ77 stage: <paramref name="input"/> is read backwards and <paramref name="output"/> filled backwards.</summary>
  internal static void Decrunch(ReadOnlySpan<byte> input, Span<byte> output) {
    var r = new BackwardBits(input);
    var o = (int)r.Long();
    if (o != output.Length)
      throw new InvalidDataException("ZAP: crunched length disagrees with its header.");
    r.Sum = r.Long();

    while (r.Bits(1) == 0) { }

    while (o > 0) {
      var x = r.Bits(2);
      if (x == 3)
        x = r.Bits(1) == 1 ? (r.Bits(1) == 1 ? -1 : r.Bits(8) + 19) : r.Bits(4) + 3;
      for (; x >= 0; --x) {
        if (--o < 0)
          throw new InvalidDataException("ZAP: crunched data overruns its output.");
        output[o] = (byte)r.Bits(8);
      }
      if (o <= 0)
        break;

      int y;
      x = r.Bits(2);
      if (x == 3) {
        x = r.Bits(1) == 1 ? (r.Bits(1) == 1 ? -1 : r.Bits(8) + 20) : r.Bits(4) + 4;
        if (x < 0)
          continue;
        y = r.Bits(1) == 1 ? r.Bits(13) + 258 : r.Bits(8) + 2;
      } else if (x != 0) {
        ++x;
        y = r.Bits(1) == 1 ? r.Bits(13) + 258 : r.Bits(8) + 2;
      } else {
        x = 1;
        y = r.Bits(8) + 2;
      }
      var match = o + y;
      if (match > output.Length)
        throw new InvalidDataException("ZAP: match reaches past the end of the output.");
      for (; x >= 0; --x) {
        if (--o < 0)
          throw new InvalidDataException("ZAP: crunched data overruns its output.");
        output[o] = output[--match];
      }
    }

    if (r.Sum != 0)
      throw new InvalidDataException("ZAP: crunched data fails its checksum.");
  }

  /// <summary>Long words read from the end towards the start; bits taken least-significant first.</summary>
  private ref struct BackwardBits(ReadOnlySpan<byte> input) {
    private readonly ReadOnlySpan<byte> _input = input;
    private int _position = input.Length;
    private uint _buffer;
    private int _left;

    public uint Sum;

    public uint Long() {
      if ((this._position -= 4) < 0)
        throw new InvalidDataException("ZAP: crunched data ends early.");
      return BinaryPrimitives.ReadUInt32BigEndian(this._input[this._position..]);
    }

    public int Bits(int n) {
      var x = 0;
      while (n-- > 0) {
        if (this._left == 0) {
          this._buffer = this.Long();
          this.Sum ^= this._buffer;
          this._left = 32;
        }
        x = (x << 1) | (int)(this._buffer & 1);
        this._buffer >>= 1;
        --this._left;
      }
      return x;
    }
  }

  /// <summary>Undoes the run-length stage: <c>in[4..8]</c> is the unpacked length, <c>in[8]</c> the escape byte.</summary>
  private static void RunLengthDecode(ReadOnlySpan<byte> input, Span<byte> output, int block) {
    if (input.Length < 9)
      throw new InvalidDataException($"ZAP: block {block} run-length header is truncated.");
    var length = BinaryPrimitives.ReadUInt32BigEndian(input[4..]);
    if (length != output.Length)
      throw new InvalidDataException($"ZAP: block {block} unpacks to {length} bytes.");
    var code = input[8];
    int i = 9, o = 0;
    while (o < output.Length) {
      if (i >= input.Length)
        throw new InvalidDataException($"ZAP: block {block} run-length data ends early.");
      var x = input[i++];
      if (x != code) {
        output[o++] = x;
        continue;
      }
      if (i >= input.Length)
        throw new InvalidDataException($"ZAP: block {block} run-length data ends early.");
      int n = input[i++];
      if (n == 0) {
        output[o++] = code;
        continue;
      }
      if (i >= input.Length || o + n + 3 > output.Length)
        throw new InvalidDataException($"ZAP: block {block} run overruns the block.");
      output.Slice(o, n + 3).Fill(input[i++]);
      o += n + 3;
    }
  }
}
