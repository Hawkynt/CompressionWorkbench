using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Entropy.Ppmd;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Decoder for the RAR 2.9 compression format (RAR 3.x/4.x archives, unpack version 29):
/// LZ77 over a sliding window with four Huffman tables, PPMd variant H blocks, and the standard
/// data filters.
/// </summary>
/// <remarks>
/// <para>
/// Converted from libarchive's RAR reader (<c>archive_read_support_format_rar.c</c>,
/// BSD-2-Clause, see <c>THIRD-PARTY-NOTICE.libarchive.txt</c>). Filters are recognised by
/// the CRC-32 and length of their RarVM program, as unrar itself does; unrar runs no other
/// programs either, so a non-standard program is refused.
/// </para>
/// <para>
/// One instance decodes one file, or the files of a solid archive in order: the window,
/// the tables, the repeat distances and pending filters carry over between calls.
/// </para>
/// </remarks>
public sealed class Rar3Decoder {
  private const int MainCodeSize = 299;
  private const int OffsetCodeSize = 60;
  private const int LowOffsetCodeSize = 17;
  private const int LengthCodeSize = 28;
  private const int TableSize = MainCodeSize + OffsetCodeSize + LowOffsetCodeSize + LengthCodeSize;
  private const int MaxCodeLength = 15;
  private const int PrecodeSize = 20;
  private const int VmWorkSize = 0x3C000;
  private const int VmUserGlobalSize = 0x2000 - 0x40;

  private static readonly byte[] LengthBases = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224];
  private static readonly byte[] LengthBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5];
  private static readonly int[] OffsetBases = [
    0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048, 3072,
    4096, 6144, 8192, 12288, 16384, 24576, 32768, 49152, 65536, 98304, 131072, 196608, 262144, 327680, 393216, 458752,
    524288, 589824, 655360, 720896, 786432, 851968, 917504, 983040, 1048576, 1310720, 1572864, 1835008, 2097152, 2359296,
    2621440, 2883584, 3145728, 3407872, 3670016, 3932160];
  private static readonly byte[] OffsetBits = [
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14,
    15, 15, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18];
  private static readonly byte[] ShortBases = [0, 4, 8, 16, 32, 64, 128, 192];
  private static readonly byte[] ShortBits = [2, 2, 3, 4, 5, 6, 6, 6];

  // Standard RarVM programs, as (CRC-32 | length << 32) of their byte code.
  private const ulong ProgramDelta = 0x1D_0E06077DUL;
  private const ulong ProgramE8 = 0x35_AD576887UL;
  private const ulong ProgramE8E9 = 0x39_3CD7E57EUL;
  private const ulong ProgramItanium = 0x78_3769893FUL;
  private const ulong ProgramRgb = 0x95_1C2C5DC8UL;
  private const ulong ProgramAudio = 0xD8_BC85E701UL;

  private static readonly byte[] ItaniumBranchMasks = [4, 4, 6, 6, 0, 0, 7, 7, 4, 4, 0, 0, 4, 4, 0, 0];

  private byte[] _window = [];
  private int _mask;
  private long _position;
  private long _fileEnd;

  private readonly byte[] _lengths = new byte[TableSize];
  private HuffmanTable? _mainCode, _offsetCode, _lowOffsetCode, _lengthCode;
  private bool _tablesRead;

  private bool _ppmBlock;
  private Ppmd7? _ppm;
  private RarPpmdRangeDecoder? _rangeDecoder;
  private int _ppmEscape = 2;
  private BitReader? _source;

  private readonly int[] _oldOffsets = new int[4];
  private int _lastOffset, _lastLength, _lastLowOffset, _lowOffsetRepeats;

  private readonly List<VmProgram> _programs = [];
  private readonly List<PendingFilter> _filters = [];
  private int _lastFilterNumber;

  private sealed class VmProgram(ulong fingerprint) {
    public readonly ulong Fingerprint = fingerprint;
    public uint OldBlockLength;
  }

  private sealed class PendingFilter(VmProgram program, long blockStart, uint blockLength, uint[] registers) {
    public readonly VmProgram Program = program;
    public readonly long BlockStart = blockStart;
    public readonly uint BlockLength = blockLength;
    public readonly uint[] Registers = registers;
  }

  /// <summary>Names of the filters applied so far, in order (diagnostics for tests).</summary>
  internal List<string> AppliedFilters { get; } = [];

  /// <summary>Decompresses the packed data of one file.</summary>
  /// <param name="compressed">The packed data.</param>
  /// <param name="unpackedSize">The file's size.</param>
  /// <param name="windowBits">Dictionary size as a power of two (16-22 for RAR 3.x/4.x).</param>
  /// <returns>The file's bytes.</returns>
  /// <exception cref="InvalidDataException">The data is corrupt.</exception>
  /// <exception cref="NotSupportedException">A filter program unrar would not run either.</exception>
  public byte[] Decompress(ReadOnlySpan<byte> compressed, int unpackedSize, int windowBits = 22) {
    var size = 1 << Math.Clamp(windowBits, 16, 26);
    if (this._window.Length < size) {
      var bigger = new byte[size];
      for (var p = Math.Max(0, this._position - this._window.Length); p < this._position; ++p)
        bigger[p & (size - 1)] = this._window[p & this._mask];
      this._window = bigger;
      this._mask = size - 1;
    }

    var fileStart = this._fileEnd;
    var fileEnd = fileStart + unpackedSize;
    this._fileEnd = fileEnd;
    var output = new byte[unpackedSize];

    // Bytes a previous file's last match already produced belong to this file.
    for (var p = fileStart; p < Math.Min(this._position, fileEnd); ++p) output[p - fileStart] = this._window[p & this._mask];

    if (unpackedSize > 0 && this._position < fileEnd) {
      var reader = this._source = new BitReader(compressed.ToArray());
      if (!this._tablesRead) this.ReadTables(reader);
      this.Expand(reader, output, fileStart, fileEnd);
    }
    if (this._position < fileEnd) throw new InvalidDataException("RAR3: packed data ends before the file does.");

    this.RunFilters(output, fileStart, fileEnd);
    return output;
  }

  // ── LZ ───────────────────────────────────────────────────────────────

  private void Expand(BitReader reader, byte[] output, long fileStart, long fileEnd) {
    while (this._position < fileEnd) {
      if (this._ppmBlock) {
        if (!this.DecodePpm(reader, output, fileStart, fileEnd)) return;
        continue;
      }

      var symbol = this._mainCode!.Decode(reader);
      if (symbol < 256) {
        this.EmitLiteral((byte)symbol, output, fileStart, fileEnd);
        continue;
      }

      int offset, length;
      switch (symbol) {
        case 256:
          if (reader.Bits(1) != 0) {
            this.ReadTables(reader);
            continue;
          }
          // End of this file's data; the next bit says whether the next file brings tables.
          this._tablesRead = reader.Bits(1) == 0;
          return;
        case 257:
          this.ReadFilter(reader);
          continue;
        case 258:
          if (this._lastLength == 0) continue;
          offset = this._lastOffset;
          length = this._lastLength;
          break;
        case <= 262: {
          var index = symbol - 259;
          offset = this._oldOffsets[index];
          var lengthSymbol = this._lengthCode!.Decode(reader);
          if (lengthSymbol >= LengthBases.Length) throw new InvalidDataException("RAR3: bad length symbol.");
          length = LengthBases[lengthSymbol] + 2 + reader.Bits(LengthBits[lengthSymbol]);
          for (var i = index; i > 0; --i) this._oldOffsets[i] = this._oldOffsets[i - 1];
          this._oldOffsets[0] = offset;
          break;
        }
        case <= 270:
          offset = ShortBases[symbol - 263] + 1 + reader.Bits(ShortBits[symbol - 263]);
          length = 2;
          this.PushOffset(offset);
          break;
        default: {
          var lengthIndex = symbol - 271;
          if (lengthIndex >= LengthBases.Length) throw new InvalidDataException("RAR3: bad length symbol.");
          length = LengthBases[lengthIndex] + 3 + reader.Bits(LengthBits[lengthIndex]);
          var offsetSymbol = this._offsetCode!.Decode(reader);
          if (offsetSymbol >= OffsetBases.Length) throw new InvalidDataException("RAR3: bad offset symbol.");
          offset = OffsetBases[offsetSymbol] + 1;
          int bits = OffsetBits[offsetSymbol];
          if (bits > 0) {
            if (offsetSymbol > 9) {
              if (bits > 4) offset += reader.Bits(bits - 4) << 4;
              if (this._lowOffsetRepeats > 0) {
                --this._lowOffsetRepeats;
                offset += this._lastLowOffset;
              } else {
                var low = this._lowOffsetCode!.Decode(reader);
                if (low == 16) {
                  this._lowOffsetRepeats = 15;
                  offset += this._lastLowOffset;
                } else {
                  offset += low;
                  this._lastLowOffset = low;
                }
              }
            } else
              offset += reader.Bits(bits);
          }
          if (offset >= 0x40000) ++length;
          if (offset >= 0x2000) ++length;
          this.PushOffset(offset);
          break;
        }
      }
      this._lastOffset = offset;
      this._lastLength = length;
      this.EmitMatch(offset, length, output, fileStart, fileEnd);
    }
    this.ConsumeEndOfFile(reader);
  }

  /// <summary>
  /// RAR ends every file's data with symbol 256: read it (and any filter defined before it) so a
  /// following solid file knows whether it brings tables. Anything else left over is padding.
  /// </summary>
  private void ConsumeEndOfFile(BitReader reader) {
    if (this._ppmBlock) {
      // PPMd ends a file with the escape byte followed by 2.
      if (reader.Remaining <= 0) return;
      try {
        if (this.PpmSymbol() == this._ppmEscape && this.PpmSymbol() == 0) this.ReadTables(reader);
      } catch (InvalidDataException) {
        // padding
      }
      return;
    }
    while (this._mainCode != null && reader.Remaining > 0) {
      int symbol;
      try {
        symbol = this._mainCode!.Decode(reader);
      } catch (InvalidDataException) {
        return;
      }
      if (symbol == 257) {
        this.ReadFilter(reader);
        continue;
      }
      if (symbol != 256) return;
      if (reader.Bits(1) != 0) {
        this.ReadTables(reader);
        continue;
      }
      this._tablesRead = reader.Bits(1) == 0;
      return;
    }
  }

  // ── PPMd ─────────────────────────────────────────────────────────────

  private int PpmSymbol() {
    var symbol = this._ppm!.DecodeSymbol(this._rangeDecoder!);
    if (symbol < 0) throw new InvalidDataException("RAR3: corrupt PPMd data.");
    return symbol;
  }

  /// <summary>One PPMd step; false at the end of the file's data.</summary>
  private bool DecodePpm(BitReader reader, byte[] output, long fileStart, long fileEnd) {
    var symbol = this.PpmSymbol();
    if (symbol != this._ppmEscape) {
      this.EmitLiteral((byte)symbol, output, fileStart, fileEnd);
      return true;
    }
    switch (this.PpmSymbol()) {
      case 0:
        this.ReadTables(reader);
        return true;
      case 2:
        return false;
      case 3: {
        var flags = this.PpmSymbol();
        var length = (flags & 7) + 1;
        if (length == 7)
          length = this.PpmSymbol() + 7;
        else if (length == 8)
          length = (this.PpmSymbol() << 8) | this.PpmSymbol();
        var code = new byte[length];
        for (var i = 0; i < length; ++i) code[i] = (byte)this.PpmSymbol();
        this.ParseFilter(code, flags);
        return true;
      }
      case 4: {
        var distance = 0;
        for (var i = 0; i < 3; ++i) distance = (distance << 8) | this.PpmSymbol();
        var length = this.PpmSymbol();
        this.EmitMatch(distance + 2, length + 32, output, fileStart, fileEnd);
        return true;
      }
      case 5:
        this.EmitMatch(1, this.PpmSymbol() + 4, output, fileStart, fileEnd);
        return true;
      default:
        // Escape followed by 1: the escape byte itself.
        this.EmitLiteral((byte)symbol, output, fileStart, fileEnd);
        return true;
    }
  }

  private void ReadPpmHeader(BitReader reader) {
    var flags = reader.Bits(7);
    var megabytes = (flags & 0x20) != 0 ? reader.Bits(8) + 1 : 0;
    this._ppmEscape = (flags & 0x40) != 0 ? reader.Bits(8) : 2;
    this._rangeDecoder = new RarPpmdRangeDecoder(() => (byte)this._source!.Bits(8));
    if ((flags & 0x20) != 0) {
      var order = (flags & 0x1F) + 1;
      if (order > 16) order = 16 + (order - 16) * 3;
      if (order == 1) throw new InvalidDataException("RAR3: bad PPMd order.");
      this._ppm ??= new Ppmd7();
      this._ppm.Allocate((uint)megabytes << 20);
      this._ppm.Initialize(order);
    } else if (this._ppm == null)
      throw new InvalidDataException("RAR3: PPMd block continues a model that was never started.");
    this._ppmBlock = true;
  }

  private void PushOffset(int offset) {
    this._oldOffsets[3] = this._oldOffsets[2];
    this._oldOffsets[2] = this._oldOffsets[1];
    this._oldOffsets[1] = this._oldOffsets[0];
    this._oldOffsets[0] = offset;
  }

  private void EmitLiteral(byte value, byte[] output, long fileStart, long fileEnd) {
    var p = this._position++;
    this._window[p & this._mask] = value;
    if (p >= fileStart && p < fileEnd) output[p - fileStart] = value;
  }

  private void EmitMatch(int offset, int length, byte[] output, long fileStart, long fileEnd) {
    var window = this._window;
    var mask = this._mask;
    for (var i = 0; i < length; ++i) {
      var p = this._position++;
      var b = window[(p - offset) & mask];
      window[p & mask] = b;
      if (p >= fileStart && p < fileEnd) output[p - fileStart] = b;
    }
  }

  // ── tables ───────────────────────────────────────────────────────────

  private void ReadTables(BitReader reader) {
    reader.AlignToByte();
    if (reader.Bits(1) != 0) {
      this.ReadPpmHeader(reader);
      return;
    }
    this._ppmBlock = false;
    this._lastLowOffset = 0;
    this._lowOffsetRepeats = 0;
    if (reader.Bits(1) == 0) Array.Clear(this._lengths);

    Span<byte> precodeLengths = stackalloc byte[PrecodeSize];
    for (var i = 0; i < PrecodeSize;) {
      var length = (byte)reader.Bits(4);
      if (length == 15) {
        var zeros = reader.Bits(4);
        if (zeros != 0) {
          for (var j = 0; j < zeros + 2 && i < PrecodeSize; ++j) precodeLengths[i++] = 0;
          continue;
        }
      }
      precodeLengths[i++] = length;
    }
    var precode = new HuffmanTable(precodeLengths);

    for (var i = 0; i < TableSize;) {
      var value = precode.Decode(reader);
      if (value < 16) {
        this._lengths[i] = (byte)((this._lengths[i] + value) & 0xF);
        ++i;
      } else if (value < 18) {
        if (i == 0) throw new InvalidDataException("RAR3: length repeat at the start of the tables.");
        var n = value == 16 ? reader.Bits(3) + 3 : reader.Bits(7) + 11;
        for (var j = 0; j < n && i < TableSize; ++j, ++i) this._lengths[i] = this._lengths[i - 1];
      } else {
        var n = value == 18 ? reader.Bits(3) + 3 : reader.Bits(7) + 11;
        for (var j = 0; j < n && i < TableSize; ++j) this._lengths[i++] = 0;
      }
    }

    var lengths = this._lengths.AsSpan();
    this._mainCode = new(lengths[..MainCodeSize]);
    this._offsetCode = new(lengths.Slice(MainCodeSize, OffsetCodeSize));
    this._lowOffsetCode = new(lengths.Slice(MainCodeSize + OffsetCodeSize, LowOffsetCodeSize));
    this._lengthCode = new(lengths.Slice(MainCodeSize + OffsetCodeSize + LowOffsetCodeSize, LengthCodeSize));
    this._tablesRead = true;
  }

  // ── filters ──────────────────────────────────────────────────────────

  private void ReadFilter(BitReader reader) {
    var flags = reader.Bits(8);
    var length = (flags & 7) + 1;
    if (length == 7)
      length = reader.Bits(8) + 7;
    else if (length == 8)
      length = reader.Bits(16);
    var code = new byte[length];
    for (var i = 0; i < length; ++i) code[i] = (byte)reader.Bits(8);
    this.ParseFilter(code, flags);
  }

  private void ParseFilter(byte[] code, int flags) {
    var br = new MemoryBits(code);
    int number;
    if ((flags & 0x80) != 0) {
      number = (int)br.VmNumber();
      if (number == 0) {
        this._filters.Clear();
        this._programs.Clear();
      } else
        --number;
      if (number > this._programs.Count) throw new InvalidDataException("RAR3: filter refers to an unknown program.");
      this._lastFilterNumber = number;
    } else
      number = this._lastFilterNumber;

    var program = number < this._programs.Count ? this._programs[number] : null;
    var blockStart = this._position + br.VmNumber();
    if ((flags & 0x40) != 0) blockStart += 258;
    var blockLength = (flags & 0x20) != 0 ? br.VmNumber() : program?.OldBlockLength ?? 0;

    var registers = new uint[8];
    registers[4] = blockLength;
    if ((flags & 0x10) != 0) {
      var mask = br.Bits(7);
      for (var i = 0; i < 7; ++i)
        if ((mask & (1 << i)) != 0) registers[i] = br.VmNumber();
    }

    if (program == null) {
      var length = br.VmNumber();
      if (length is 0 or > 0x10000) throw new InvalidDataException("RAR3: bad filter program length.");
      var bytecode = new byte[length];
      for (var i = 0; i < length; ++i) bytecode[i] = (byte)br.Bits(8);
      byte xor = 0;
      for (var i = 1; i < bytecode.Length; ++i) xor ^= bytecode[i];
      if (xor != bytecode[0]) throw new InvalidDataException("RAR3: filter program fails its check byte.");
      program = new(Crc32.Compute(bytecode) | ((ulong)length << 32));
      this._programs.Add(program);
    }
    program.OldBlockLength = blockLength;

    if ((flags & 0x08) != 0) {
      var globalLength = br.VmNumber();
      if (globalLength > VmUserGlobalSize) throw new InvalidDataException("RAR3: filter global data too large.");
      for (var i = 0; i < globalLength; ++i) br.Bits(8);
    }
    if (br.Overrun) throw new InvalidDataException("RAR3: truncated filter.");

    this._filters.Add(new(program, blockStart, blockLength, registers));
  }

  private void RunFilters(byte[] output, long fileStart, long fileEnd) {
    var applied = 0;
    foreach (var filter in this._filters) {
      if (filter.BlockStart >= fileEnd) break;
      ++applied;
      var start = filter.BlockStart - fileStart;
      if (start < 0 || start + filter.BlockLength > output.Length)
        throw new InvalidDataException("RAR3: filter block lies outside the file.");
      var block = output.AsSpan((int)start, (int)filter.BlockLength);
      this.Execute(filter, block, (uint)start);
    }
    this._filters.RemoveRange(0, applied);
  }

  private void Execute(PendingFilter filter, Span<byte> block, uint filePosition) {
    var r = filter.Registers;
    switch (filter.Program.Fingerprint) {
      case ProgramE8:
      case ProgramE8E9:
        UnE8(block, filePosition, filter.Program.Fingerprint == ProgramE8E9);
        this.AppliedFilters.Add(filter.Program.Fingerprint == ProgramE8E9 ? "E8E9" : "E8");
        break;
      case ProgramDelta:
        UnDelta(block, r[0]);
        this.AppliedFilters.Add("Delta");
        break;
      case ProgramRgb:
        UnRgb(block, r[0], r[1]);
        this.AppliedFilters.Add("Rgb");
        break;
      case ProgramAudio:
        UnAudio(block, r[0]);
        this.AppliedFilters.Add("Audio");
        break;
      case ProgramItanium:
        UnItanium(block, filePosition);
        this.AppliedFilters.Add("Itanium");
        break;
      default:
        throw new NotSupportedException("RAR3: the archive uses a RarVM program other than the standard filters.");
    }
  }

  private static void UnE8(Span<byte> data, uint filePosition, bool e9) {
    if (data.Length > VmWorkSize || data.Length <= 4) throw new InvalidDataException("RAR3: bad E8 filter block.");
    const uint FileSize = 0x1000000;
    for (var i = 0; i <= data.Length - 5; ++i) {
      if (data[i] != 0xE8 && !(e9 && data[i] == 0xE9)) continue;
      var current = filePosition + (uint)i + 1;
      var address = BinaryPrimitives.ReadInt32LittleEndian(data[(i + 1)..]);
      if (address < 0 && current >= (uint)-address)
        BinaryPrimitives.WriteInt32LittleEndian(data[(i + 1)..], (int)(address + FileSize));
      else if (address >= 0 && (uint)address < FileSize)
        BinaryPrimitives.WriteInt32LittleEndian(data[(i + 1)..], (int)(address - current));
      i += 4;
    }
  }

  private static void UnDelta(Span<byte> data, uint channels) {
    if (data.Length > VmWorkSize / 2) throw new InvalidDataException("RAR3: bad delta filter block.");
    var source = data.ToArray();
    var s = 0;
    for (var c = 0; c < channels; ++c) {
      byte last = 0;
      for (var i = c; i < data.Length; i += (int)channels) {
        if (s >= source.Length) throw new InvalidDataException("RAR3: bad delta filter block.");
        data[i] = last = (byte)(last - source[s++]);
      }
    }
  }

  private static void UnRgb(Span<byte> data, uint stride, uint redOffset) {
    var n = data.Length;
    if (n > VmWorkSize / 2 || stride > n || n < 3 || redOffset > 2) throw new InvalidDataException("RAR3: bad RGB filter block.");
    var source = data.ToArray();
    var dst = new byte[n + 3];
    var s = 0;
    for (var c = 0; c < 3; ++c) {
      byte value = 0;
      for (var j = c; j < n; j += 3) {
        // Paeth-like choice between the pixel one row up and the one up-right of it.
        var above = j - (int)stride;
        if (above >= 0) {
          int upLeft = dst[above], up = dst[above + 3];
          var d1 = Math.Abs(up - upLeft);
          var d2 = Math.Abs(value - upLeft);
          var d3 = Math.Abs(up - upLeft + value - upLeft);
          if (d1 > d2 || d1 > d3) value = d2 <= d3 ? (byte)up : (byte)upLeft;
        }
        value = (byte)(value - source[s++]);
        dst[j] = value;
      }
    }
    for (var i = (int)redOffset; i < n - 2; i += 3) {
      dst[i] += dst[i + 1];
      dst[i + 2] += dst[i + 1];
    }
    dst.AsSpan(0, n).CopyTo(data);
  }

  private static void UnAudio(Span<byte> data, uint channels) {
    if (data.Length > VmWorkSize / 2) throw new InvalidDataException("RAR3: bad audio filter block.");
    var source = data.ToArray();
    var s = 0;
    Span<int> weight = stackalloc int[3];
    Span<int> delta = stackalloc int[3];
    Span<uint> error = stackalloc uint[7];
    for (var c = 0; c < channels; ++c) {
      weight.Clear();
      delta.Clear();
      error.Clear();
      int lastDelta = 0, count = 0;
      byte lastByte = 0;
      for (var j = c; j < data.Length; j += (int)channels) {
        if (s >= source.Length) throw new InvalidDataException("RAR3: bad audio filter block.");
        var d = (sbyte)source[s++];
        delta[2] = delta[1];
        delta[1] = lastDelta - delta[0];
        delta[0] = lastDelta;
        var predicted = (byte)((8 * lastByte + weight[0] * delta[0] + weight[1] * delta[1] + weight[2] * delta[2]) >> 3);
        var value = (byte)(predicted - d);
        var e = d << 3;
        error[0] += (uint)Math.Abs(e);
        error[1] += (uint)Math.Abs(e - delta[0]);
        error[2] += (uint)Math.Abs(e + delta[0]);
        error[3] += (uint)Math.Abs(e - delta[1]);
        error[4] += (uint)Math.Abs(e + delta[1]);
        error[5] += (uint)Math.Abs(e - delta[2]);
        error[6] += (uint)Math.Abs(e + delta[2]);
        lastDelta = (sbyte)(value - lastByte);
        data[j] = lastByte = value;
        if ((count++ & 0x1F) == 0) {
          var best = 0;
          for (var k = 1; k < 7; ++k)
            if (error[k] < error[best]) best = k;
          error.Clear();
          switch (best) {
            case 1: if (weight[0] >= -16) --weight[0]; break;
            case 2: if (weight[0] < 16) ++weight[0]; break;
            case 3: if (weight[1] >= -16) --weight[1]; break;
            case 4: if (weight[1] < 16) ++weight[1]; break;
            case 5: if (weight[2] >= -16) --weight[2]; break;
            case 6: if (weight[2] < 16) ++weight[2]; break;
          }
        }
      }
    }
  }

  /// <summary>IA-64: branch targets in 128-bit bundles become relative again (bundle-granular file offset).</summary>
  private static void UnItanium(Span<byte> data, uint filePosition) {
    if (data.Length > VmWorkSize || data.Length < 21) throw new InvalidDataException("RAR3: bad IA-64 filter block.");
    var bundle = filePosition >> 4;
    for (var at = 0; at < data.Length - 21; at += 16, ++bundle) {
      var template = (data[at] & 0x1F) - 0x10;
      if (template < 0) continue;
      var slots = ItaniumBranchMasks[template];
      for (var slot = 0; slot < 3; ++slot) {
        if ((slots & (1 << slot)) == 0) continue;
        var start = slot * 41 + 5;
        if (BundleBits(data[at..], start + 37, 4) != 5) continue;
        var target = BundleBits(data[at..], start + 13, 20);
        SetBundleBits(data[at..], (target - bundle) & 0xFFFFF, start + 13, 20);
      }
    }
  }

  private static uint BundleBits(ReadOnlySpan<byte> bundle, int bit, int count) {
    var word = BinaryPrimitives.ReadUInt32LittleEndian(bundle[(bit >> 3)..]);
    return (word >> (bit & 7)) & ((1u << count) - 1);
  }

  private static void SetBundleBits(Span<byte> bundle, uint value, int bit, int count) {
    var at = bit >> 3;
    var shift = bit & 7;
    var mask = ~(((1u << count) - 1) << shift);
    var word = BinaryPrimitives.ReadUInt32LittleEndian(bundle[at..]);
    BinaryPrimitives.WriteUInt32LittleEndian(bundle[at..], (word & mask) | (value << shift));
  }

  // ── bit input ────────────────────────────────────────────────────────

  /// <summary>Bits most significant first; reading past the end yields zeros up to a small slack.</summary>
  internal sealed class BitReader(byte[] data) {
    private readonly byte[] _data = data;
    private long _bit;

    public int Bits(int n) {
      if (n == 0) return 0;
      var v = this.Peek(n);
      this.Skip(n);
      return v;
    }

    public int Peek(int n) {
      var at = (int)(this._bit >> 3);
      uint word = 0;
      for (var i = 0; i < 4; ++i) word = (word << 8) | (at + i < this._data.Length ? this._data[at + i] : 0u);
      return (int)((word << (int)(this._bit & 7)) >> (32 - n));
    }

    public void Skip(int n) {
      this._bit += n;
      if (this._bit > (long)this._data.Length * 8 + 64) throw new InvalidDataException("RAR3: unexpected end of packed data.");
    }

    public void AlignToByte() => this._bit = (this._bit + 7) & ~7L;

    /// <summary>Bits left before the end of the data.</summary>
    public long Remaining => (long)this._data.Length * 8 - this._bit;
  }

  /// <summary>Filter parameters are read from their own byte string, most significant bit first.</summary>
  private sealed class MemoryBits(byte[] data) {
    private int _bit;

    public bool Overrun { get; private set; }

    public uint Bits(int n) {
      uint v = 0;
      for (var i = 0; i < n; ++i, ++this._bit) {
        var at = this._bit >> 3;
        if (at >= data.Length) {
          this.Overrun = true;
          return 0;
        }
        v = (v << 1) | (uint)((data[at] >> (7 - (this._bit & 7))) & 1);
      }
      return v;
    }

    public uint VmNumber() {
      switch (this.Bits(2)) {
        case 0: return this.Bits(4);
        case 1: {
          var v = this.Bits(8);
          return v >= 16 ? v : 0xFFFFFF00 | (v << 4) | this.Bits(4);
        }
        case 2: return this.Bits(16);
        default: return this.Bits(32);
      }
    }
  }

  /// <summary>Canonical Huffman code (codes assigned by length, then by symbol), table driven.</summary>
  internal sealed class HuffmanTable {
    private readonly short[] _symbols;
    private readonly byte[] _lengths;
    private readonly int _bits;

    public HuffmanTable(ReadOnlySpan<byte> codeLengths) {
      var max = 0;
      foreach (var l in codeLengths) max = Math.Max(max, l);
      this._bits = Math.Max(1, max);
      this._symbols = new short[1 << this._bits];
      this._lengths = new byte[1 << this._bits];
      Array.Fill(this._symbols, (short)-1);
      var code = 0;
      for (var length = 1; length <= max; ++length) {
        for (var symbol = 0; symbol < codeLengths.Length; ++symbol) {
          if (codeLengths[symbol] != length) continue;
          var first = code << (this._bits - length);
          var count = 1 << (this._bits - length);
          if (first + count > this._symbols.Length) throw new InvalidDataException("RAR3: oversubscribed Huffman table.");
          for (var j = 0; j < count; ++j) {
            this._symbols[first + j] = (short)symbol;
            this._lengths[first + j] = (byte)length;
          }
          ++code;
        }
        code <<= 1;
      }
    }

    public int Decode(BitReader reader) {
      var index = reader.Peek(this._bits);
      var symbol = this._symbols[index];
      if (symbol < 0) throw new InvalidDataException("RAR3: invalid Huffman code.");
      reader.Skip(this._lengths[index]);
      return symbol;
    }
  }
}
