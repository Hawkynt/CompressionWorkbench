using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Streaming decoder for the RAR 5.0 compression format (RAR 5.x-7.x archives, compression
/// algorithm version 0): LZ77 over a sliding window, four Huffman tables per block, and the
/// delta, x86 (E8, E8E9) and ARM filters.
/// </summary>
/// <remarks>
/// <para>
/// Converted from libarchive's RAR 5 reader (<c>libarchive/archive_read_support_format_rar5.c</c>,
/// v3.8.10, Copyright (c) 2018 Grzegorz Antoniak, BSD-2-Clause, see
/// <c>THIRD-PARTY-NOTICE.libarchive.txt</c>): block header, table decoding, the symbol loop,
/// filter scheduling and the three filters follow it. The container-level parts of that
/// reader (volume switching, archive headers) are not part of this class; the caller hands
/// in the packed bytes of one file as a stream, already decrypted and joined across volumes.
/// </para>
/// <para>
/// One instance decodes one file, or the files of a solid stream in order: the window, the
/// tables, the repeat distances and the last length carry over between calls when
/// <c>solid</c> is set. Memory is bounded by the window plus one block (at most 16 MB) and
/// one filter (at most 4 MB); input and output are streamed.
/// </para>
/// </remarks>
public sealed class Rar5Unpacker {
  private const int HuffBc = 20;
  private const int HuffNc = 306;
  private const int HuffDc = 64;
  private const int HuffLdc = 16;
  private const int HuffRc = 44;
  private const int HuffTableSize = HuffNc + HuffDc + HuffRc + HuffLdc;
  private const int MaxFilterLength = 0x400000;
  private const int FlushMargin = 0x8000; // above the longest match (4100 bytes)

  private enum FilterType { Delta = 0, E8 = 1, E8E9 = 2, Arm = 3 }

  private sealed class Filter {
    public FilterType Type;
    public int Channels;
    public long BlockStart;
    public int BlockLength;
  }

  private sealed class DecodeTable {
    public int Size;
    public readonly int[] DecodeLen = new int[16];
    public readonly int[] DecodePos = new int[16];
    public int QuickBits;
    public readonly byte[] QuickLen = new byte[1 << 10];
    public readonly ushort[] QuickNum = new ushort[1 << 10];
    public readonly ushort[] DecodeNum = new ushort[HuffNc];
  }

  private readonly byte[] _window;
  private readonly long _windowMask;
  private byte[] _filtered = [];

  private readonly DecodeTable _bd = new();
  private readonly DecodeTable _ld = new();
  private readonly DecodeTable _dd = new();
  private readonly DecodeTable _ldd = new();
  private readonly DecodeTable _rd = new();
  private bool _tablesRead;

  private readonly int[] _distCache = new int[4];
  private int _lastLen;
  private readonly Queue<Filter> _filters = new();
  private long _lastFilterStart;
  private int _lastFilterLength;

  // Positions: _solidOffset is where the current file starts in the window stream;
  // _writePtr counts bytes the current file has produced, _lastWritePtr those flushed.
  private long _solidOffset;
  private long _writePtr;
  private long _lastWritePtr;
  private bool _started;

  // Current block.
  private byte[] _block = new byte[64];
  private int _blockSize;
  private int _inAddr;
  private int _bitAddr;
  private int _blockBitSize;
  private bool _blockLast;
  private bool _blockFinished = true;

  /// <summary>Creates a decoder whose window holds <paramref name="windowSize"/> bytes.</summary>
  /// <param name="windowSize">Window size, rounded up to a power of two; at least the dictionary
  /// the stream needs, or the total size of the stream when that is smaller.</param>
  /// <exception cref="NotSupportedException">The window would exceed 1 GiB.</exception>
  public Rar5Unpacker(long windowSize) {
    var size = 1L << 17;
    while (size < windowSize) size <<= 1;
    if (size > 1L << 30)
      throw new NotSupportedException($"A RAR 5 window of {size >> 20} MiB is beyond what this decoder allocates (1 GiB).");
    this._window = new byte[size];
    this._windowMask = size - 1;
  }

  /// <summary>The allocated window size in bytes.</summary>
  public long WindowSize => this._window.LongLength;

  /// <summary>
  /// The window a stream needs: the dictionary it declares, capped by the bytes it holds
  /// (at least 4 MB, the longest filter, unless the stream is shorter).
  /// </summary>
  public static long WindowFor(long dictionarySize, long streamSize) {
    // The margin keeps a window that holds the whole stream from ever filling up, so pending
    // filters and unflushed output can always wait in it.
    var need = Math.Min(dictionarySize, streamSize + FlushMargin * 2);
    var filterRoom = Math.Min(streamSize + FlushMargin * 2, MaxFilterLength * 2L);
    return Math.Max(Math.Max(need, filterRoom), 1L << 17);
  }

  /// <summary>
  /// Decodes one file. <paramref name="input"/> is read sequentially from its current position;
  /// at most <paramref name="packedSize"/> bytes are consumed.
  /// </summary>
  /// <param name="input">The packed data.</param>
  /// <param name="packedSize">Bytes of packed data available.</param>
  /// <param name="unpackedSize">The file size, or -1 when the header leaves it unknown.</param>
  /// <param name="output">Receives the unpacked bytes.</param>
  /// <param name="solid">Continue the window and tables of the previous file.</param>
  /// <returns>The number of bytes written.</returns>
  /// <exception cref="InvalidDataException">The stream is malformed or truncated.</exception>
  public long Unpack(Stream input, long packedSize, long unpackedSize, Stream output, bool solid) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (solid && !this._started)
      throw new InvalidDataException("RAR 5 solid file without a preceding file in its stream.");
    this.StartFile(solid);
    this._started = true;

    var remaining = packedSize;
    var limit = unpackedSize < 0 ? long.MaxValue : unpackedSize;
    if (limit == 0)
      return 0;

    var stalled = 0;
    while (true) {
      var before = this._writePtr + this._lastWritePtr + this._inAddr;
      if (this._blockFinished) {
        if (this._blockLast || remaining <= 0) {
          if (unpackedSize >= 0 && this._writePtr < limit)
            throw new InvalidDataException("RAR 5 stream ends before the file does.");
          break;
        }
        this.ReadBlock(input, ref remaining);
      }

      this.DecodeBlock(limit);
      this.ApplyFiltersAndFlush(output, final: false);
      if (this._writePtr >= limit)
        break;
      if (this._writePtr + this._lastWritePtr + this._inAddr == before && !this._blockFinished && ++stalled > 2)
        throw new InvalidDataException("RAR 5 filter does not fit the window.");
    }

    this.ApplyFiltersAndFlush(output, final: true);
    if (this._filters.Count > 0)
      throw new InvalidDataException("RAR 5 filter reaches beyond the end of the file.");
    return this._lastWritePtr;
  }

  private void StartFile(bool solid) {
    if (solid) {
      this._solidOffset += this._writePtr;
    } else {
      this._solidOffset = 0;
      this._tablesRead = false;
      Array.Clear(this._distCache);
      this._lastLen = 0;
    }
    this._writePtr = 0;
    this._lastWritePtr = 0;
    this._filters.Clear();
    this._lastFilterStart = 0;
    this._lastFilterLength = 0;
    this._blockFinished = true;
    this._blockLast = false;
  }

  private void ReadBlock(Stream input, ref long remaining) {
    Span<byte> head = stackalloc byte[2];
    if (remaining < 3)
      throw new InvalidDataException("Truncated RAR 5 block header.");
    ReadExactly(input, head);
    var flags = head[0];
    var byteCount = (flags >> 3) & 7;
    if (byteCount > 2)
      throw new InvalidDataException($"Unsupported RAR 5 block header size {byteCount + 1}.");
    var sizeBytes = byteCount + 1;
    Span<byte> sz = stackalloc byte[3];
    sz.Clear();
    ReadExactly(input, sz[..sizeBytes]);
    var blockSize = sz[0] | (sz[1] << 8) | (sz[2] << 16);
    var check = (byte)(0x5A ^ flags ^ sz[0] ^ sz[1] ^ sz[2]);
    if (check != head[1])
      throw new InvalidDataException("RAR 5 block header checksum mismatch.");
    remaining -= 2 + sizeBytes;
    if (blockSize > remaining)
      throw new InvalidDataException("RAR 5 block reaches beyond the packed data.");

    if (this._block.Length < blockSize + 8)
      this._block = new byte[Math.Max(blockSize + 8, this._block.Length * 2)];
    ReadExactly(input, this._block.AsSpan(0, blockSize));
    this._block.AsSpan(blockSize, 8).Clear();
    remaining -= blockSize;

    this._blockSize = blockSize;
    this._blockBitSize = (flags & 7) + 1;
    this._blockLast = (flags & 0x40) != 0;
    this._inAddr = 0;
    this._bitAddr = 0;
    this._blockFinished = false;

    if ((flags & 0x80) != 0) {
      this.ParseTables();
      this._tablesRead = true;
    } else if (!this._tablesRead)
      throw new InvalidDataException("RAR 5 block without Huffman tables at the start of a stream.");
  }

  private static void ReadExactly(Stream s, Span<byte> buffer) {
    var done = 0;
    while (done < buffer.Length) {
      var n = s.Read(buffer[done..]);
      if (n <= 0)
        throw new InvalidDataException("Unexpected end of RAR 5 packed data.");
      done += n;
    }
  }

  // ---- bit reader over the current block ----

  private uint ReadBits16() {
    if (this._inAddr >= this._blockSize)
      throw new InvalidDataException("Premature end of RAR 5 block.");
    var p = this._block;
    var i = this._inAddr;
    var bits = (uint)((p[i] << 16) | (p[i + 1] << 8) | p[i + 2]);
    return (bits >> (8 - this._bitAddr)) & 0xFFFF;
  }

  private uint ReadBits32() {
    if (this._inAddr >= this._blockSize)
      throw new InvalidDataException("Premature end of RAR 5 block.");
    var p = this._block;
    var i = this._inAddr;
    var bits = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(i));
    bits <<= this._bitAddr;
    bits |= (uint)(p[i + 4] >> (8 - this._bitAddr));
    return bits;
  }

  private void SkipBits(int bits) {
    var n = this._bitAddr + bits;
    this._inAddr += n >> 3;
    this._bitAddr = n & 7;
  }

  private int ReadConsumeBits(int n) {
    var v = (int)(this.ReadBits16() >> (16 - n));
    this.SkipBits(n);
    return v;
  }

  // ---- Huffman tables ----

  private void ParseTables() {
    Span<byte> bitLength = stackalloc byte[HuffBc];
    var table = new byte[HuffTableSize];
    var p = this._block;
    int w = 0, i = 0;
    var nibbleMask = 0xF0;
    var nibbleShift = 4;
    while (w < HuffBc) {
      if (i >= this._blockSize)
        throw new InvalidDataException("Truncated RAR 5 Huffman tables.");
      var value = (p[i] & nibbleMask) >> nibbleShift;
      if (nibbleMask == 0x0F) ++i;
      nibbleMask ^= 0xFF;
      nibbleShift ^= 4;
      if (value == 15) {
        value = (p[i] & nibbleMask) >> nibbleShift;
        if (nibbleMask == 0x0F) ++i;
        nibbleMask ^= 0xFF;
        nibbleShift ^= 4;
        if (value == 0)
          bitLength[w++] = 15;
        else
          for (var k = 0; k < value + 2 && w < HuffBc; ++k)
            bitLength[w++] = 0;
      } else
        bitLength[w++] = (byte)value;
    }
    this._inAddr = i;
    this._bitAddr = nibbleShift ^ 4;
    CreateDecodeTables(bitLength, this._bd, HuffBc);

    for (i = 0; i < HuffTableSize;) {
      var num = this.DecodeNumber(this._bd);
      if (num < 16) {
        table[i++] = (byte)num;
      } else if (num < 18) {
        var n = this.ReadBits16();
        if (num == 16) { n = (n >> 13) + 3; this.SkipBits(3); }
        else { n = (n >> 9) + 11; this.SkipBits(7); }
        if (i == 0)
          throw new InvalidDataException("RAR 5 table repeats a length before the first one.");
        while (n-- > 0 && i < HuffTableSize) {
          table[i] = table[i - 1];
          ++i;
        }
      } else {
        var n = this.ReadBits16();
        if (num == 18) { n = (n >> 13) + 3; this.SkipBits(3); }
        else { n = (n >> 9) + 11; this.SkipBits(7); }
        while (n-- > 0 && i < HuffTableSize)
          table[i++] = 0;
      }
    }

    var idx = 0;
    CreateDecodeTables(table.AsSpan(idx, HuffNc), this._ld, HuffNc); idx += HuffNc;
    CreateDecodeTables(table.AsSpan(idx, HuffDc), this._dd, HuffDc); idx += HuffDc;
    CreateDecodeTables(table.AsSpan(idx, HuffLdc), this._ldd, HuffLdc); idx += HuffLdc;
    CreateDecodeTables(table.AsSpan(idx, HuffRc), this._rd, HuffRc);
  }

  private static void CreateDecodeTables(ReadOnlySpan<byte> bitLength, DecodeTable table, int size) {
    Span<int> lc = stackalloc int[16];
    lc.Clear();
    Array.Clear(table.DecodeNum);
    table.Size = size;
    table.QuickBits = size == HuffNc ? 10 : 7;

    for (var i = 0; i < size; ++i)
      ++lc[bitLength[i] & 15];
    lc[0] = 0;
    table.DecodePos[0] = 0;
    table.DecodeLen[0] = 0;

    var upperLimit = 0;
    for (var i = 1; i < 16; ++i) {
      upperLimit += lc[i];
      table.DecodeLen[i] = upperLimit << (16 - i);
      table.DecodePos[i] = table.DecodePos[i - 1] + lc[i - 1];
      upperLimit <<= 1;
    }
    if (upperLimit > 65536)
      throw new InvalidDataException("Over-subscribed RAR 5 Huffman table.");

    Span<int> posClone = stackalloc int[16];
    table.DecodePos.AsSpan().CopyTo(posClone);
    for (var i = 0; i < size; ++i) {
      var clen = bitLength[i] & 15;
      if (clen > 0)
        table.DecodeNum[posClone[clen]++] = (ushort)i;
    }

    var quickDataSize = 1 << table.QuickBits;
    var curLen = 1;
    for (var code = 0; code < quickDataSize; ++code) {
      var bitField = code << (16 - table.QuickBits);
      while (curLen < 16 && bitField >= table.DecodeLen[curLen])
        ++curLen;
      table.QuickLen[code] = (byte)curLen;
      var dist = (bitField - table.DecodeLen[curLen - 1]) >> (16 - curLen);
      var pos = table.DecodePos[curLen & 15] + dist;
      table.QuickNum[code] = curLen < 16 && pos < size ? table.DecodeNum[pos] : (ushort)0;
    }
  }

  private int DecodeNumber(DecodeTable table) {
    var bitField = (int)(this.ReadBits16() & 0xFFFE);
    if (bitField < table.DecodeLen[table.QuickBits]) {
      var code = bitField >> (16 - table.QuickBits);
      this.SkipBits(table.QuickLen[code]);
      return table.QuickNum[code];
    }
    var bits = 15;
    for (var i = table.QuickBits + 1; i < 15; ++i)
      if (bitField < table.DecodeLen[i]) { bits = i; break; }
    this.SkipBits(bits);
    var dist = (bitField - table.DecodeLen[bits - 1]) >> (16 - bits);
    var pos = table.DecodePos[bits] + dist;
    if (pos >= table.Size) pos = 0;
    return table.DecodeNum[pos];
  }

  // ---- symbol loop ----

  private int DecodeCodeLength(int code) {
    int lbits, length = 2;
    if (code < 8) {
      lbits = 0;
      length += code;
    } else {
      lbits = code / 4 - 1;
      length += (4 | (code & 3)) << lbits;
    }
    if (lbits > 0)
      length += this.ReadConsumeBits(lbits);
    return length;
  }

  private void CopyString(int len, long dist, long limit) {
    if (len > limit - this._writePtr)
      throw new InvalidDataException("RAR 5 data exceeds the declared file size.");
    var mask = this._windowMask;
    var w = this._window;
    var writePos = this._solidOffset + this._writePtr;
    if (dist > writePos)
      throw new InvalidDataException("RAR 5 match reaches before the start of the stream.");
    var from = writePos - dist;
    for (var i = 0; i < len; ++i)
      w[(writePos + i) & mask] = w[(from + i) & mask];
    this._writePtr += len;
  }

  private void DecodeBlock(long limit) {
    var mask = this._windowMask;
    var full = this._window.LongLength - FlushMargin;
    while (true) {
      if (this._writePtr - this._lastWritePtr > full)
        return;
      if (this._inAddr > this._blockSize - 1 ||
          (this._inAddr == this._blockSize - 1 && this._bitAddr >= this._blockBitSize)) {
        this._blockFinished = true;
        return;
      }
      if (this._writePtr >= limit) {
        // The file is complete; whatever bits remain belong to nothing.
        this._blockFinished = true;
        return;
      }

      var num = this.DecodeNumber(this._ld);
      if (num < 256) {
        if (this._writePtr >= limit)
          throw new InvalidDataException("RAR 5 data exceeds the declared file size.");
        this._window[(this._solidOffset + this._writePtr++) & mask] = (byte)num;
        continue;
      }

      if (num >= 262) {
        var len = this.DecodeCodeLength(num - 262);
        var distSlot = this.DecodeNumber(this._dd);
        long dist = 1;
        int dbits;
        if (distSlot < 4) {
          dbits = 0;
          dist += distSlot;
        } else {
          dbits = distSlot / 2 - 1;
          dist += (long)(uint)(2 | (distSlot & 1)) << dbits;
        }
        if (dbits > 0) {
          if (dbits >= 4) {
            if (dbits > 4) {
              var add = this.ReadBits32();
              this.SkipBits(dbits - 4);
              dist += (long)(add >> (36 - dbits)) << 4;
            }
            dist += this.DecodeNumber(this._ldd);
          } else
            dist += this.ReadConsumeBits(dbits);
        }
        if (dist > 0x100) {
          ++len;
          if (dist > 0x2000) {
            ++len;
            if (dist > 0x40000)
              ++len;
          }
        }
        if (dist > int.MaxValue)
          throw new InvalidDataException("RAR 5 distance overflow.");
        this._distCache[3] = this._distCache[2];
        this._distCache[2] = this._distCache[1];
        this._distCache[1] = this._distCache[0];
        this._distCache[0] = (int)dist;
        this._lastLen = len;
        this.CopyString(len, dist, limit);
      } else if (num == 256) {
        this.ParseFilter();
      } else if (num == 257) {
        if (this._lastLen != 0)
          this.CopyString(this._lastLen, this._distCache[0], limit);
      } else {
        var idx = num - 258;
        var dist = this._distCache[idx];
        for (var i = idx; i > 0; --i)
          this._distCache[i] = this._distCache[i - 1];
        this._distCache[0] = dist;
        var lenSlot = this.DecodeNumber(this._rd);
        var len = this.DecodeCodeLength(lenSlot);
        this._lastLen = len;
        this.CopyString(len, dist, limit);
      }
    }
  }

  // ---- filters ----

  private uint ParseFilterData() {
    var bytes = this.ReadConsumeBits(2) + 1;
    uint data = 0;
    for (var i = 0; i < bytes; ++i) {
      var b = this.ReadBits16();
      data += (b >> 8) << (i * 8);
      this.SkipBits(8);
    }
    return data;
  }

  private void ParseFilter() {
    var blockStart = this.ParseFilterData();
    var blockLength = this.ParseFilterData();
    var type = (int)(this.ReadBits16() >> 13);
    this.SkipBits(3);

    var start = this._writePtr + blockStart;
    if (blockLength < 4 || blockLength > MaxFilterLength
        || (this._lastFilterLength != 0 && start < this._lastFilterStart + this._lastFilterLength))
      throw new InvalidDataException("Invalid RAR 5 filter.");
    if (type > (int)FilterType.Arm)
      throw new InvalidDataException($"Unknown RAR 5 filter type {type}.");

    var f = new Filter { Type = (FilterType)type, BlockStart = start, BlockLength = (int)blockLength };
    this._lastFilterStart = start;
    this._lastFilterLength = (int)blockLength;
    if (f.Type == FilterType.Delta)
      f.Channels = this.ReadConsumeBits(5) + 1;
    this._filters.Enqueue(f);
  }

  private void ApplyFiltersAndFlush(Stream output, bool final) {
    while (true) {
      if (this._filters.TryPeek(out var f)) {
        if (this._writePtr > f.BlockStart && this._writePtr >= f.BlockStart + f.BlockLength) {
          if (this._lastWritePtr == f.BlockStart) {
            this.RunFilter(f, output);
            this._filters.Dequeue();
          } else {
            this.FlushWindow(output, this._lastWritePtr, f.BlockStart);
          }
          continue;
        }
        var end = Math.Min(f.BlockStart, this._writePtr);
        if (end > this._lastWritePtr)
          this.FlushWindow(output, this._lastWritePtr, end);
        return;
      }
      if (this._writePtr > this._lastWritePtr)
        this.FlushWindow(output, this._lastWritePtr, this._writePtr);
      return;
    }
  }

  private void FlushWindow(Stream output, long begin, long end) {
    var mask = this._windowMask;
    var size = this._window.LongLength;
    var b = this._solidOffset + begin;
    var e = this._solidOffset + end;
    while (b < e) {
      var start = (int)(b & mask);
      var count = (int)Math.Min(e - b, size - start);
      output.Write(this._window, start, count);
      b += count;
    }
    this._lastWritePtr = end;
  }

  private byte WindowAt(long filePos) => this._window[(this._solidOffset + filePos) & this._windowMask];

  private uint ReadWindowU32(long filePos) {
    var v = 0u;
    for (var k = 0; k < 4; ++k)
      v |= (uint)this.WindowAt(filePos + k) << (k * 8);
    return v;
  }

  private void RunFilter(Filter f, Stream output) {
    if (this._filtered.Length < f.BlockLength)
      this._filtered = new byte[Math.Max(f.BlockLength, 1 << 16)];
    var dst = this._filtered;
    var len = f.BlockLength;
    switch (f.Type) {
      case FilterType.Delta: {
        var src = 0L;
        for (var c = 0; c < f.Channels; ++c) {
          byte prev = 0;
          for (var d = c; d < len; d += f.Channels) {
            prev -= this.WindowAt(f.BlockStart + src++);
            dst[d] = prev;
          }
        }
        break;
      }
      case FilterType.E8:
      case FilterType.E8E9: {
        const uint fileSize = 0x1000000;
        for (var k = 0; k < len; ++k)
          dst[k] = this.WindowAt(f.BlockStart + k);
        var extended = f.Type == FilterType.E8E9;
        for (var i = 0; i < len - 4;) {
          var b = this.WindowAt(f.BlockStart + i++);
          if (b == 0xE8 || (extended && b == 0xE9)) {
            var offset = (uint)((i + f.BlockStart) % fileSize);
            var addr = this.ReadWindowU32(f.BlockStart + i);
            if ((addr & 0x80000000) != 0) {
              if (((addr + offset) & 0x80000000) == 0)
                BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(i), addr + fileSize);
            } else if (((addr - fileSize) & 0x80000000) != 0) {
              BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(i), addr - offset);
            }
            i += 4;
          }
        }
        break;
      }
      case FilterType.Arm: {
        for (var k = 0; k < len; ++k)
          dst[k] = this.WindowAt(f.BlockStart + k);
        for (var i = 0; i < len - 3; i += 4) {
          if (this.WindowAt(f.BlockStart + i + 3) == 0xEB) {
            var offset = this.ReadWindowU32(f.BlockStart + i) & 0x00FFFFFF;
            offset -= (uint)((i + f.BlockStart) / 4);
            offset = (offset & 0x00FFFFFF) | 0xEB000000;
            BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(i), offset);
          }
        }
        break;
      }
    }
    output.Write(dst, 0, len);
    this._lastWritePtr += len;
  }
}
