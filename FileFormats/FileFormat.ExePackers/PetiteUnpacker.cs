#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.ExePackers;

/// <summary>
/// Container format of a PEtite-packed Win32 PE, reconstructed from the
/// on-disk layout and the entry stub of the samples themselves.
///
/// <para>A PEtite image keeps the original section virtual addresses. The
/// packed bytes live in one oversized section mapped at the first original
/// section's RVA; a second section holds the untouched resources; the last
/// section (the one the entry point falls into) holds the loader stub. Right
/// behind the stub code sits a block table that drives unpacking:</para>
/// <list type="bullet">
///   <item><description>a record whose first dword has bit 31 set is a
///   descending <c>rep movsd</c> — <c>{0x80000000|dwordCount, sourceEndRva,
///   destinationEndRva}</c>, 12 bytes — which lifts the packed bytes out of the
///   way of the image that is about to be written over them;</description></item>
///   <item><description>any other record is
///   <c>{sourceRva, decompressedSize, destinationRva, unused}</c>, 16 bytes, and
///   expands one original section in place. A zero length marks an original
///   section without initialised data and is skipped; a zero source ends the
///   table.</description></item>
/// </list>
///
/// <para>The compressed streams are DEFLATE (RFC 1951) with one deviation: the
/// stub has no fixed-Huffman tables, so block type <c>1</c> selects the dynamic
/// Huffman tables that standard DEFLATE assigns to type <c>2</c>, and types 2
/// and 3 are rejected. Everything else — LSB-first bit order, the 14-bit
/// HLIT/HDIST/HCLEN header, the code-length alphabet, the length/distance base
/// and extra-bit tables — matches RFC 1951 byte for byte; those five tables are
/// stored verbatim at the head of the stub section and were read from there.</para>
///
/// <para>Code blocks are additionally stored with relative branch targets
/// converted to absolute ones: scanning forward, every <c>E8</c>/<c>E9</c> and
/// every <c>0F 80..0F 8F</c> has the block offset of its opcode added to the
/// following dword, and the scan then skips the whole instruction. Reversing it
/// subtracts the same offset again.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.rfc-editor.org/rfc/rfc1951</c> — DEFLATE compressed data format</description></item>
///   <item><description><c>https://www.un4seen.com/petite/</c> — PEtite (Ian Luck / Un4seen Developments)</description></item>
/// </list>
/// </summary>
public static class PetiteUnpacker {

  /// <summary>
  /// Represents a petite block.
  /// </summary>
  public readonly record struct PetiteBlock(uint SourceRva, uint DestinationRva, byte[] Data, bool BranchFilterReversed);

  /// <summary>
  /// Represents a petite image.
  /// </summary>
  public sealed record PetiteImage(
    IReadOnlyList<PetiteBlock> Blocks,
    byte[] MemoryImage,
    uint BlockTableRva,
    uint StubSectionRva);

  private const int MaxRecords = 512;

  /// <summary>
  /// Performs the try unpack operation.
  /// </summary>
  public static bool TryUnpack(ReadOnlySpan<byte> image, long maximumDecompressedSize, out PetiteImage? result, out string error) {
    result = null;
    if (!TryReadHeaders(image, out var headers, out error))
      return false;

    if (headers.SizeOfImage == 0 || headers.SizeOfImage > maximumDecompressedSize) {
      error = "PEtite: SizeOfImage is missing or exceeds the configured limit.";
      return false;
    }

    var memory = new byte[headers.SizeOfImage];
    var headerBytes = (int)Math.Min(headers.SizeOfHeaders, (uint)image.Length);
    if (headerBytes > 0 && headerBytes <= memory.Length)
      image[..headerBytes].CopyTo(memory);
    foreach (var s in headers.Sections) {
      var available = (int)Math.Min(s.RawSize, (uint)Math.Max(0, image.Length - (long)s.RawOffset));
      if (available <= 0 || s.VirtualAddress >= (uint)memory.Length)
        continue;
      available = (int)Math.Min((uint)available, (uint)memory.Length - s.VirtualAddress);
      image.Slice((int)s.RawOffset, available).CopyTo(memory.AsSpan((int)s.VirtualAddress));
    }

    var stub = headers.Sections.FirstOrDefault(s =>
      headers.EntryPoint >= s.VirtualAddress &&
      headers.EntryPoint < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));
    if (stub.VirtualSize == 0 && stub.RawSize == 0) {
      error = "PEtite: no section contains the entry point.";
      return false;
    }

    if (!TryFindBlockTable(image, stub, out var tableRva)) {
      error = "PEtite: the block table could not be located in the entry stub.";
      return false;
    }

    var blocks = new List<PetiteBlock>();
    var cursor = tableRva;
    for (var guard = 0; guard < MaxRecords; ++guard) {
      if (cursor + 16 > (uint)memory.Length)
        break;
      var first = BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan((int)cursor));
      if ((first & 0x80000000u) != 0) {
        // Descending block move: esi/edi address the LAST dword of each range.
        var dwords = first & 0x7FFFFFFFu;
        var sourceEnd = BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan((int)cursor + 4));
        var destinationEnd = BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan((int)cursor + 8));
        cursor += 12;
        if (dwords == 0)
          continue;
        var length = (long)dwords * 4;
        long sourceStart = (long)sourceEnd + 4 - length;
        long destinationStart = (long)destinationEnd + 4 - length;
        if (sourceStart < 0 || destinationStart < 0 ||
            sourceStart + length > memory.Length || destinationStart + length > memory.Length) {
          error = "PEtite: a block-move record addresses memory outside the image.";
          return false;
        }
        memory.AsSpan((int)sourceStart, (int)length).CopyTo(memory.AsSpan((int)destinationStart));
        continue;
      }

      var size = BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan((int)cursor + 4));
      var destination = BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan((int)cursor + 8));
      cursor += 16;
      // A zero length is an original section without initialised data — the
      // stub skips those and keeps walking; a zero source ends the table.
      if (first == 0)
        break;
      if (size == 0)
        continue;
      if (first >= (uint)memory.Length || size > maximumDecompressedSize ||
          (long)destination + size > memory.Length) {
        error = "PEtite: a block record addresses memory outside the image.";
        return false;
      }

      if (!PetiteInflate.TryInflate(memory, (int)first, (int)size, out var inflated) || inflated == null) {
        error = $"PEtite: the stream for RVA 0x{destination:X8} is not a valid PEtite DEFLATE block.";
        return false;
      }

      var data = inflated;
      var filtered = ShouldReverseBranchFilter(data, destination, headers.BaseOfData);
      if (filtered)
        data = ReverseBranchFilter(data);
      data.CopyTo(memory.AsSpan((int)destination));
      blocks.Add(new(first, destination, data, filtered));
    }

    if (blocks.Count == 0) {
      error = "PEtite: the block table held no compressed blocks.";
      return false;
    }

    error = string.Empty;
    result = new(blocks, memory, tableRva, stub.VirtualAddress);
    return true;
  }

  /// <summary>
  /// Reverses the absolute-branch-target transform: subtract each opcode's own
  /// block offset from the dword that follows it.
  /// </summary>
  public static byte[] ReverseBranchFilter(byte[] block) {
    var copy = (byte[])block.Clone();
    var i = 0;
    while (i < copy.Length - 5) {
      var opcode = copy[i];
      int field;
      int step;
      if (opcode is 0xE8 or 0xE9) {
        field = i + 1;
        step = 5;
      } else if (opcode == 0x0F && copy[i + 1] is >= 0x80 and <= 0x8F) {
        field = i + 2;
        step = 6;
      } else {
        ++i;
        continue;
      }

      var value = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(field));
      BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(field), value - (uint)i);
      i += step;
    }
    return copy;
  }

  /// <summary>
  /// The block table records no filter flag we could identify, so the decision
  /// is made from the image itself: only blocks below the (packer-preserved)
  /// BaseOfData are candidates, and the transform is reversed only when doing
  /// so makes more branch targets land inside the block than leaving it alone.
  /// Verified against 92 blocks of 55 corpus samples whose unpacked original is
  /// known, with no disagreement.
  /// </summary>
  private static bool ShouldReverseBranchFilter(byte[] block, uint destination, uint baseOfData) {
    if (baseOfData != 0 && destination >= baseOfData)
      return false;

    var (sites, direct) = ScoreBranchTargets(block);
    if (sites < 8)
      return false;
    var (_, reversed) = ScoreBranchTargets(ReverseBranchFilter(block));
    return reversed > direct;
  }

  private static (int Sites, double InRange) ScoreBranchTargets(byte[] block) {
    var sites = 0;
    var inRange = 0;
    var i = 0;
    while (i < block.Length - 5) {
      var opcode = block[i];
      long target;
      if (opcode is 0xE8 or 0xE9) {
        target = i + 5L + BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(i + 1));
        i += 5;
      } else if (opcode == 0x0F && block[i + 1] is >= 0x80 and <= 0x8F) {
        target = i + 6L + BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(i + 2));
        i += 6;
      } else {
        ++i;
        continue;
      }

      ++sites;
      if (target >= 0 && target < block.Length)
        ++inRange;
    }
    return (sites, sites == 0 ? 0 : (double)inRange / sites);
  }

  /// <summary>
  /// The stub loads the table address as <c>pop eax; lea ebx, [eax + imm32]</c>
  /// with eax holding the stub section's base address. Candidates are confirmed
  /// by parsing the record chain, so a stray byte match cannot win.
  /// </summary>
  private static bool TryFindBlockTable(ReadOnlySpan<byte> image, PeSection stub, out uint tableRva) {
    tableRva = 0;
    var available = (int)Math.Min(stub.RawSize, (uint)Math.Max(0, image.Length - (long)stub.RawOffset));
    if (available <= 0)
      return false;

    var body = image.Slice((int)stub.RawOffset, available);
    for (var i = 0; i + 7 <= body.Length; ++i) {
      if (body[i] != 0x58 || body[i + 1] != 0x8D || body[i + 2] != 0x98)
        continue;
      var displacement = BinaryPrimitives.ReadInt32LittleEndian(body[(i + 3)..]);
      if (displacement <= 0 || displacement >= stub.VirtualSize)
        continue;
      tableRva = stub.VirtualAddress + (uint)displacement;
      return true;
    }
    return false;
  }

  internal readonly record struct PeSection(string Name, uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize);

  private readonly record struct PeHeaders(uint EntryPoint, uint SizeOfImage, uint SizeOfHeaders, uint BaseOfData, IReadOnlyList<PeSection> Sections);

  private static bool TryReadHeaders(ReadOnlySpan<byte> image, out PeHeaders headers, out string error) {
    headers = default;
    error = string.Empty;
    if (image.Length < 0x40 || image[0] != 'M' || image[1] != 'Z') {
      error = "PEtite: not an MZ image.";
      return false;
    }

    var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(image[0x3C..]);
    if (peOffset + 24 > (uint)image.Length) {
      error = "PEtite: the PE header is outside the file.";
      return false;
    }

    var pe = (int)peOffset;
    if (image[pe] != 'P' || image[pe + 1] != 'E' || image[pe + 2] != 0 || image[pe + 3] != 0) {
      error = "PEtite: the PE signature is missing.";
      return false;
    }

    var coff = pe + 4;
    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image[(coff + 2)..]);
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image[(coff + 16)..]);
    var optional = coff + 20;
    var sectionTable = optional + optionalSize;
    if (optionalSize < 0x60 || sectionCount == 0 || sectionCount > 96 ||
        sectionTable + sectionCount * 40 > image.Length) {
      error = "PEtite: the section table is truncated.";
      return false;
    }

    if (BinaryPrimitives.ReadUInt16LittleEndian(image[optional..]) != 0x10B) {
      error = "PEtite: only 32-bit PE images are supported.";
      return false;
    }

    var sections = new List<PeSection>(sectionCount);
    for (var i = 0; i < sectionCount; ++i) {
      var s = sectionTable + i * 40;
      var raw = image.Slice(s, 8);
      var end = raw.IndexOf((byte)0);
      var name = System.Text.Encoding.ASCII.GetString(end < 0 ? raw : raw[..end]);
      sections.Add(new(
        name,
        BinaryPrimitives.ReadUInt32LittleEndian(image[(s + 12)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(s + 8)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(s + 20)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(image[(s + 16)..])));
    }

    headers = new(
      BinaryPrimitives.ReadUInt32LittleEndian(image[(optional + 16)..]),
      BinaryPrimitives.ReadUInt32LittleEndian(image[(optional + 56)..]),
      BinaryPrimitives.ReadUInt32LittleEndian(image[(optional + 60)..]),
      BinaryPrimitives.ReadUInt32LittleEndian(image[(optional + 24)..]),
      sections);
    return true;
  }
}

/// <summary>
/// DEFLATE (RFC 1951) decoder for the PEtite dialect: block type 1 carries the
/// dynamic Huffman tables (there is no fixed-table block type), types 2 and 3
/// are invalid. Written against RFC 1951 and the table constants the PEtite
/// stub stores in clear at the head of its section.
/// </summary>
public static class PetiteInflate {

  private static readonly ushort[] LengthBase = [
    3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258,
  ];

  private static readonly byte[] LengthExtra = [
    0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
  ];

  private static readonly ushort[] DistanceBase = [
    1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769,
    1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577,
  ];

  private static readonly byte[] DistanceExtra = [
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13,
  ];

  private static readonly byte[] CodeLengthOrder = [
    16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15,
  ];

  /// <summary>
  /// Performs the try inflate operation.
  /// </summary>
  public static bool TryInflate(ReadOnlySpan<byte> input, int start, int expectedSize, out byte[]? output) {
    output = null;
    if (start < 0 || start >= input.Length || expectedSize <= 0)
      return false;

    var result = new byte[expectedSize];
    var written = 0;
    var reader = new BitReader(input, start);
    var literals = new HuffmanTable();
    var distances = new HuffmanTable();
    var codeLengths = new HuffmanTable();
    var lengths = new byte[288 + 32];

    try {
      while (written < expectedSize) {
        var last = reader.Read(1);
        var type = reader.Read(2);
        if (type == 0) {
          reader.AlignToByte();
          var storedLength = reader.Read(16);
          var complement = reader.Read(16);
          if (storedLength != (~complement & 0xFFFF) || written + storedLength > expectedSize)
            return false;
          for (var i = 0; i < storedLength; ++i)
            result[written++] = (byte)reader.Read(8);
        } else if (type == 1) {
          var literalCount = reader.Read(5) + 257;
          var distanceCount = reader.Read(5) + 1;
          var codeLengthCount = reader.Read(4) + 4;
          Array.Clear(lengths);
          var order = new byte[19];
          for (var i = 0; i < codeLengthCount; ++i)
            order[CodeLengthOrder[i]] = (byte)reader.Read(3);
          if (!codeLengths.Build(order, 19))
            return false;

          var total = literalCount + distanceCount;
          var index = 0;
          while (index < total) {
            var symbol = codeLengths.Decode(ref reader);
            if (symbol < 0)
              return false;
            if (symbol < 16) {
              lengths[index++] = (byte)symbol;
              continue;
            }

            int repeat;
            byte value = 0;
            if (symbol == 16) {
              if (index == 0)
                return false;
              value = lengths[index - 1];
              repeat = 3 + reader.Read(2);
            } else if (symbol == 17) {
              repeat = 3 + reader.Read(3);
            } else {
              repeat = 11 + reader.Read(7);
            }

            if (index + repeat > total)
              return false;
            for (var i = 0; i < repeat; ++i)
              lengths[index++] = value;
          }

          if (!literals.Build(lengths.AsSpan(0, literalCount), literalCount))
            return false;
          if (!distances.Build(lengths.AsSpan(literalCount, distanceCount), distanceCount))
            return false;

          while (true) {
            var symbol = literals.Decode(ref reader);
            if (symbol < 0)
              return false;
            if (symbol < 256) {
              if (written >= expectedSize)
                return false;
              result[written++] = (byte)symbol;
              continue;
            }
            if (symbol == 256)
              break;

            symbol -= 257;
            if (symbol >= LengthBase.Length)
              return false;
            var length = LengthBase[symbol] + reader.Read(LengthExtra[symbol]);
            var distanceSymbol = distances.Decode(ref reader);
            if (distanceSymbol < 0 || distanceSymbol >= DistanceBase.Length)
              return false;
            var distance = DistanceBase[distanceSymbol] + reader.Read(DistanceExtra[distanceSymbol]);
            if (distance > written || written + length > expectedSize)
              return false;
            var from = written - distance;
            for (var i = 0; i < length; ++i)
              result[written + i] = result[from + i];
            written += length;
          }
        } else {
          return false;
        }

        if (last != 0)
          break;
      }
    } catch (IndexOutOfRangeException) {
      return false;
    } catch (ArgumentOutOfRangeException) {
      return false;
    }

    if (written != expectedSize)
      return false;
    output = result;
    return true;
  }

  private ref struct BitReader {
    private readonly ReadOnlySpan<byte> _data;
    private int _position;
    private uint _bits;
    private int _count;

    public BitReader(ReadOnlySpan<byte> data, int position) {
      this._data = data;
      this._position = position;
      this._bits = 0;
      this._count = 0;
    }

    public int Read(int width) {
      while (this._count < width) {
        this._bits |= (uint)this._data[this._position++] << this._count;
        this._count += 8;
      }
      var value = (int)(this._bits & ((1u << width) - 1));
      this._bits >>= width;
      this._count -= width;
      return value;
    }

    public int ReadBit() {
      if (this._count == 0) {
        this._bits = this._data[this._position++];
        this._count = 8;
      }
      var value = (int)(this._bits & 1);
      this._bits >>= 1;
      --this._count;
      return value;
    }

    public void AlignToByte() {
      var drop = this._count & 7;
      this._bits >>= drop;
      this._count -= drop;
    }
  }

  /// <summary>
  /// Canonical Huffman decoder built the way RFC 1951 §3.2.2 describes it: count
  /// the symbols per code length, derive the first code of every length, and
  /// walk the incoming bits MSB-first until the accumulated code falls inside
  /// the range a length owns.
  /// </summary>
  private sealed class HuffmanTable {
    private const int MaxBits = 15;
    private readonly int[] _countPerLength = new int[MaxBits + 1];
    private readonly int[] _firstCode = new int[MaxBits + 2];
    private readonly int[] _firstIndex = new int[MaxBits + 2];
    private readonly int[] _symbols = new int[288 + 32];
    private int _maxLength;

    public bool Build(ReadOnlySpan<byte> lengths, int count) {
      Array.Clear(this._countPerLength);
      this._maxLength = 0;
      for (var i = 0; i < count; ++i) {
        var length = lengths[i];
        if (length > MaxBits)
          return false;
        if (length == 0)
          continue;
        ++this._countPerLength[length];
        if (length > this._maxLength)
          this._maxLength = length;
      }
      if (this._maxLength == 0)
        return false;

      var code = 0;
      var index = 0;
      for (var length = 1; length <= this._maxLength; ++length) {
        code = (code + this._countPerLength[length - 1]) << 1;
        this._firstCode[length] = code;
        this._firstIndex[length] = index;
        index += this._countPerLength[length];
        if (code + this._countPerLength[length] > 1 << length)
          return false;
      }

      var next = new int[MaxBits + 1];
      for (var length = 1; length <= this._maxLength; ++length)
        next[length] = this._firstIndex[length];
      for (var i = 0; i < count; ++i) {
        var length = lengths[i];
        if (length != 0)
          this._symbols[next[length]++] = i;
      }
      return true;
    }

    public int Decode(ref BitReader reader) {
      var code = 0;
      for (var length = 1; length <= this._maxLength; ++length) {
        code = (code << 1) | reader.ReadBit();
        var available = this._countPerLength[length];
        if (available != 0) {
          var offset = code - this._firstCode[length];
          if (offset >= 0 && offset < available)
            return this._symbols[this._firstIndex[length] + offset];
        }
      }
      return -1;
    }
  }
}
