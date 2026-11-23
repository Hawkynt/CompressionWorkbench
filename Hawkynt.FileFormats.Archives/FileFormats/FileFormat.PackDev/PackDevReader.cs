using System.Buffers.Binary;
using System.Text;

namespace FileFormat.PackDev;

/// <summary>
/// Reads an Amiga PackDev device image ("PKD\x13", or the older "PKD\x11") and rebuilds the device's
/// blocks.
/// </summary>
/// <remarks>
/// <para>Header (big-endian): magic, block count, block size, reserved blocks, track length in bytes,
/// XPK buffer size, XPK packer id (0 = stored). "PKD\x13" then carries a 20-byte packer name; both end
/// with the XPK mode (u16) and a flag (u16) saying whether a free-block map follows — 52 bytes in all,
/// 32 for "PKD\x11".</para>
/// <para>The free-block map is block count / 8 bytes: big-endian 32-bit words whose bit j (counted from
/// the least significant) is block <c>reserved + 32·w + j</c>; a set bit marks a free block, which is
/// not stored and reads back as zeros. Stored images then hold, track by track, every stored block
/// of the track followed by a 16-byte sector label per stored block.</para>
/// <para>XPK-packed images (packer id ≠ 0) wrap the same byte stream in XPK buffers; they are refused,
/// as no such image has been available to prove a reader.</para>
/// <para>Sourcing: layout from libxad's PackDev client (The Unarchiver's XADMaster,
/// <c>libxad/clients/PackDev.c</c>, LGPL-2.1-or-later); verified on real images, whose AmigaDOS
/// volumes come out intact.</para>
/// </remarks>
public sealed class PackDevReader {
  /// <summary>Bytes of one sector label after each stored block.</summary>
  public const int LabelSize = 16;

  private const long MaxImage = 1L << 30;

  private readonly byte[] _data;
  private readonly int _payload;

  /// <summary>Number of blocks on the device.</summary>
  public int BlockCount { get; }

  /// <summary>Bytes per block.</summary>
  public int BlockSize { get; }

  /// <summary>Leading blocks the free-block map does not cover.</summary>
  public int ReservedBlocks { get; }

  /// <summary>Blocks per track.</summary>
  public int TrackBlocks { get; }

  /// <summary>XPK packer id (0 when the blocks are stored).</summary>
  public uint XpkPacker { get; }

  /// <summary>The XPK packer name of a "PKD\x13" image, empty otherwise.</summary>
  public string PackerName { get; }

  /// <summary>True for the older "PKD\x11" header.</summary>
  public bool IsOldFormat { get; }

  /// <summary>True when a free-block map is present and free blocks were left out.</summary>
  public bool HasFreeBlockMap { get; }

  /// <summary>Size of the rebuilt image in bytes.</summary>
  public long ImageSize => (long)this.BlockCount * this.BlockSize;

  /// <summary>Returns true when <paramref name="data"/> starts with a PackDev magic.</summary>
  public static bool IsPackDev(ReadOnlySpan<byte> data) =>
    data.Length >= 4 && data[..3].SequenceEqual("PKD"u8) && data[3] is 0x11 or 0x13;

  /// <summary>Reads and checks the header.</summary>
  /// <exception cref="InvalidDataException">Not a PackDev image, or its geometry is impossible.</exception>
  public PackDevReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    var d = this._data;
    if (!IsPackDev(d))
      throw new InvalidDataException("PackDev: missing PKD header.");
    this.IsOldFormat = d[3] == 0x11;
    var headerSize = this.IsOldFormat ? 32 : 52;
    if (d.Length < headerSize)
      throw new InvalidDataException("PackDev: truncated header.");
    var blocks = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(4));
    var blockSize = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(8));
    var reserved = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(12));
    var trackLength = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(16));
    this.XpkPacker = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(24));
    if (blocks == 0 || blockSize == 0 || blockSize > 0x10000 || reserved > blocks
        || trackLength < blockSize || trackLength % blockSize != 0 || (long)blocks * blockSize > MaxImage)
      throw new InvalidDataException("PackDev: impossible device geometry.");
    this.BlockCount = (int)blocks;
    this.BlockSize = (int)blockSize;
    this.ReservedBlocks = (int)reserved;
    this.TrackBlocks = (int)(trackLength / blockSize);
    this.PackerName = this.IsOldFormat ? "" : Encoding.Latin1.GetString(d.AsSpan(28, 20)).TrimEnd('\0');
    this.HasFreeBlockMap = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(headerSize - 2)) != 0;
    this._payload = headerSize;
  }

  /// <summary>Rebuilds the device image; free blocks are zero.</summary>
  /// <exception cref="InvalidDataException">The data ends early or has bytes left over.</exception>
  /// <exception cref="NotSupportedException">The image is XPK-packed.</exception>
  public byte[] ExtractImage() {
    if (this.XpkPacker != 0)
      throw new NotSupportedException("PackDev: XPK-packed images are not supported.");
    var d = this._data;
    var pos = this._payload;
    var free = new bool[this.BlockCount];
    if (this.HasFreeBlockMap) {
      var mapSize = this.BlockCount >> 3;
      if (pos + mapSize > d.Length)
        throw new InvalidDataException("PackDev: truncated free-block map.");
      var map = d.AsSpan(pos, mapSize);
      for (var i = 0; i < this.BlockCount - this.ReservedBlocks; ++i) {
        var at = (i >> 5) << 2;
        if (at >= map.Length) break;
        var word = at + 4 <= map.Length ? BinaryPrimitives.ReadUInt32BigEndian(map[at..]) : TailWord(map[at..]);
        if ((word & (1u << (i & 31))) != 0)
          free[i + this.ReservedBlocks] = true;
      }
      pos += mapSize;
    }

    var image = new byte[this.ImageSize];
    var storedInTrack = 0;
    for (var block = 0; block < this.BlockCount; ++block) {
      if (!free[block]) {
        if (pos + this.BlockSize > d.Length)
          throw new InvalidDataException($"PackDev: data ends inside block {block}.");
        d.AsSpan(pos, this.BlockSize).CopyTo(image.AsSpan(block * this.BlockSize));
        pos += this.BlockSize;
        ++storedInTrack;
      }
      if (block % this.TrackBlocks == this.TrackBlocks - 1) {
        pos += LabelSize * storedInTrack;
        storedInTrack = 0;
      }
    }
    if (pos != d.Length)
      throw new InvalidDataException($"PackDev: {d.Length - pos} bytes do not match the stored blocks.");
    return image;
  }

  private static uint TailWord(ReadOnlySpan<byte> tail) {
    Span<byte> w = stackalloc byte[4];
    tail.CopyTo(w);
    return BinaryPrimitives.ReadUInt32BigEndian(w);
  }
}
