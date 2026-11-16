using System.Buffers.Binary;
using Compression.Core.Checksums;

namespace Compression.Core.Dictionary.Lz5;

/// <summary>
/// The LZ5 1.5 frame: the LZ4 frame layout under its own magic, with LZ5 blocks and LZ5's block
/// sizes (identifier 1-7 for 64 KiB × 4^(id−1), up to 256 MiB).
/// </summary>
/// <remarks>
/// Converted from lz5 1.5 <c>lib/lz5frame.c</c> (<c>LZ5F_decodeHeader</c>, <c>LZ5F_getBlockSize</c>,
/// the block and checksum handling) — see THIRD-PARTY-NOTICE.lz5.txt. Header: UInt32 magic
/// 0x184D2205; FLG (version 01, block independence, block checksum — which LZ5 1.5 refuses, content
/// size, content checksum); BD (block size identifier in bits 4-6); an optional UInt64 content size;
/// a header checksum byte (bits 8-15 of the XXH32 of FLG, BD and the content size). Blocks: UInt32
/// size, bit 31 set for a stored block; a zero size ends the frame, followed by the XXH32 of the
/// content when flagged. Linked blocks may reach 4 MiB back into earlier blocks of the frame.
/// Skippable frames (magic 0x184D2A5x) are passed over.
/// </remarks>
public static class Lz5Frame {
  /// <summary>The frame magic.</summary>
  public const uint Magic = 0x184D2205;

  private const uint SkippableMask = 0xFFFFFFF0;
  private const uint SkippableMagic = 0x184D2A50;

  /// <summary>The maximum block size of block size identifier <paramref name="id"/> (1-7).</summary>
  public static int BlockSize(int id) => id is >= 1 and <= 7
    ? (64 * 1024) << (2 * (id - 1))
    : throw new InvalidDataException($"LZ5: invalid block size identifier {id}.");

  /// <summary>
  /// Writes <paramref name="data"/> as one frame — independent blocks of at most 4 MiB, content
  /// size and content checksum present — to <paramref name="output"/>.
  /// </summary>
  public static void Write(ReadOnlySpan<byte> data, Stream output) {
    const int blockSizeId = 4;
    const byte flags = 0x40 | 0x20 | 0x08 | 0x04;
    var blockSize = BlockSize(blockSizeId);

    Span<byte> header = stackalloc byte[15];
    BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
    header[4] = flags;
    header[5] = blockSizeId << 4;
    BinaryPrimitives.WriteUInt64LittleEndian(header[6..], (ulong)data.Length);
    header[14] = (byte)(XxHash32.Compute(header[4..14]) >> 8);
    output.Write(header);

    var encoded = new byte[Lz5Block.MaxEncodedLength(Math.Min(blockSize, data.Length))];
    Span<byte> word = stackalloc byte[4];
    for (var offset = 0; offset < data.Length;) {
      var block = data.Slice(offset, Math.Min(blockSize, data.Length - offset));
      var length = Lz5Block.Encode(block, encoded);
      if (length < block.Length) {
        BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)length);
        output.Write(word);
        output.Write(encoded, 0, length);
      } else {
        BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)block.Length | 0x80000000u);
        output.Write(word);
        output.Write(block);
      }
      offset += block.Length;
    }

    BinaryPrimitives.WriteUInt32LittleEndian(word, 0);
    output.Write(word);
    BinaryPrimitives.WriteUInt32LittleEndian(word, XxHash32.Compute(data));
    output.Write(word);
  }

  /// <summary>
  /// Decodes every frame in <paramref name="data"/> (skippable frames passed over) to
  /// <paramref name="output"/>. Memory holds one block and at most 4 MiB of history.
  /// </summary>
  /// <exception cref="InvalidDataException">The data is not a sequence of valid LZ5 frames.</exception>
  public static void Decode(ReadOnlySpan<byte> data, Stream output) {
    var pos = 0;
    byte[] window = [];
    while (pos < data.Length) {
      var magic = ReadUInt32(data, ref pos);
      if ((magic & SkippableMask) == SkippableMagic) {
        var skip = ReadUInt32(data, ref pos);
        if (skip > (uint)(data.Length - pos))
          throw new InvalidDataException("LZ5: a skippable frame runs past the end of the data.");
        pos += (int)skip;
        continue;
      }
      if (magic != Magic)
        throw new InvalidDataException($"LZ5: not an LZ5 frame (magic 0x{magic:X8}).");
      pos = DecodeFrame(data, pos, output, ref window);
    }
  }

  private static int DecodeFrame(ReadOnlySpan<byte> data, int pos, Stream output, ref byte[] window) {
    if (data.Length - pos < 3)
      throw new InvalidDataException("LZ5: truncated frame header.");
    var flags = data[pos];
    var descriptor = data[pos + 1];
    if ((flags >> 6) != 1)
      throw new InvalidDataException("LZ5: unsupported frame version.");
    if ((flags & 0x03) != 0 || (descriptor & 0x8F) != 0)
      throw new InvalidDataException("LZ5: reserved frame header bits are set.");
    var independent = (flags & 0x20) != 0;
    var blockChecksum = (flags & 0x10) != 0;
    var hasContentSize = (flags & 0x08) != 0;
    var contentChecksum = (flags & 0x04) != 0;
    var maxBlock = BlockSize((descriptor >> 4) & 7);
    var headerLength = hasContentSize ? 10 : 2;
    if (data.Length - pos < headerLength + 1)
      throw new InvalidDataException("LZ5: truncated frame header.");
    if (data[pos + headerLength] != (byte)(XxHash32.Compute(data.Slice(pos, headerLength)) >> 8))
      throw new InvalidDataException("LZ5: frame header checksum mismatch.");
    long? contentSize = hasContentSize ? (long)BinaryPrimitives.ReadUInt64LittleEndian(data[(pos + 2)..]) : null;
    pos += headerLength + 1;

    var history = independent ? 0 : Lz5Block.MaxDistance + 1;
    if (window.Length < history + maxBlock)
      window = new byte[history + maxBlock];
    var hash = contentChecksum ? new XxHash32() : null;
    var kept = 0;
    var total = 0L;
    while (true) {
      var size = ReadUInt32(data, ref pos);
      if (size == 0)
        break;
      var stored = (size & 0x80000000u) != 0;
      size &= 0x7FFFFFFFu;
      if (size > maxBlock || size > (uint)(data.Length - pos))
        throw new InvalidDataException("LZ5: a block runs past its frame.");
      var block = data.Slice(pos, (int)size);
      pos += (int)size;
      if (blockChecksum && ReadUInt32(data, ref pos) != XxHash32.Compute(block))
        throw new InvalidDataException("LZ5: block checksum mismatch.");

      int written;
      if (stored) {
        block.CopyTo(window.AsSpan(kept));
        written = block.Length;
      } else
        written = Lz5Block.Decode(block, window.AsSpan(0, kept + maxBlock), kept);
      output.Write(window, kept, written);
      hash?.Update(window.AsSpan(kept, written));
      total += written;

      // Linked blocks keep the last 4 MiB of output in front of the next block.
      var end = kept + written;
      var keep = Math.Min(end, history);
      if (keep > 0 && end != keep)
        window.AsSpan(end - keep, keep).CopyTo(window);
      kept = keep;
    }

    if (contentChecksum && ReadUInt32(data, ref pos) != hash!.Value)
      throw new InvalidDataException("LZ5: content checksum mismatch.");
    if (contentSize is { } expected && expected != total)
      throw new InvalidDataException($"LZ5: the frame declares {expected} bytes but holds {total}.");
    return pos;
  }

  private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int pos) {
    if (data.Length - pos < 4)
      throw new InvalidDataException("LZ5: truncated frame.");
    var value = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
    pos += 4;
    return value;
  }
}
