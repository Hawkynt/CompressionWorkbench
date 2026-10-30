using System.Buffers.Binary;
using System.Text;

namespace FileFormat.QuadrupleD;

/// <summary>One file of a Quadruple D archive.</summary>
/// <param name="Name">File name.</param>
/// <param name="Offset">Offset of the file's data in the archive.</param>
/// <param name="StoredSize">Bytes the data occupies in the archive.</param>
/// <param name="Size">Original size in bytes.</param>
public sealed record QuadrupleDEntry(string Name, long Offset, long StoredSize, long Size);

/// <summary>
/// Reads Quadruple D (QDA0) game archives: a 256-byte header, a directory of 268-byte records and
/// the file data, either stored or compressed with byte pair encoding.
/// </summary>
/// <remarks>
/// <para>
/// Header: a 32-bit little-endian flag (1 = byte pair encoded, 0 = stored), the signature
/// <c>QDA0</c> and the file count, then zeros up to offset 0x100. Each directory record holds the
/// data offset, the stored size and the original size (32-bit little-endian), then a 256-byte
/// zero-terminated name.
/// </para>
/// <para>
/// The byte pair encoding is Philip Gage's ("A New Algorithm for Data Compression", C/C++ Users
/// Journal, February 1994): blocks, each a pair table followed by its packed data. The one
/// difference from the article's program is the length of the packed data, which is a 32-bit
/// little-endian number here rather than a 16-bit big-endian one. That was measured on real
/// archives: with it, every file decodes to exactly its recorded size from exactly its stored bytes.
/// </para>
/// </remarks>
public static class QuadrupleDArchive {
  /// <summary>The signature at offset 4.</summary>
  public static ReadOnlySpan<byte> Signature => "QDA0"u8;

  private const int HeaderSize = 0x100;
  private const int RecordSize = 12 + 256;
  private const int FlagStored = 0;
  private const int FlagBytePairs = 1;

  /// <summary>Whether <paramref name="data"/> starts like a Quadruple D archive.</summary>
  public static bool IsQuadrupleD(ReadOnlySpan<byte> data)
    => data.Length >= 12 && data.Slice(4, 4).SequenceEqual(Signature) && BinaryPrimitives.ReadUInt32LittleEndian(data) is FlagStored or FlagBytePairs;

  /// <summary>Whether the archive's files are byte pair encoded.</summary>
  public static bool IsCompressed(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32LittleEndian(data) == FlagBytePairs;

  /// <summary>Lists the archive's files.</summary>
  /// <exception cref="InvalidDataException">The data is not a well-formed Quadruple D archive.</exception>
  public static List<QuadrupleDEntry> List(ReadOnlySpan<byte> data) {
    if (!IsQuadrupleD(data)) throw new InvalidDataException("Not a Quadruple D archive.");
    var count = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
    if (count > (data.Length - HeaderSize) / RecordSize) throw new InvalidDataException("Quadruple D directory runs past the end.");
    var entries = new List<QuadrupleDEntry>((int)count);
    for (var i = 0; i < count; ++i) {
      var record = data.Slice(HeaderSize + i * RecordSize, RecordSize);
      long offset = BinaryPrimitives.ReadUInt32LittleEndian(record);
      long stored = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
      long size = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
      if (offset + stored > data.Length) throw new InvalidDataException($"Quadruple D file {i} runs past the end.");
      entries.Add(new QuadrupleDEntry(DecodeName(record[12..]), offset, stored, size));
    }
    return entries;
  }

  /// <summary>Extracts one file.</summary>
  /// <exception cref="InvalidDataException">The file's data is corrupt.</exception>
  public static byte[] Extract(ReadOnlySpan<byte> data, QuadrupleDEntry entry) {
    var stored = data.Slice((int)entry.Offset, (int)entry.StoredSize);
    if (!IsCompressed(data)) {
      if (entry.StoredSize != entry.Size) throw new InvalidDataException($"Stored Quadruple D file '{entry.Name}' has two different sizes.");
      return stored.ToArray();
    }
    return ExpandBytePairs(stored, entry.Size);
  }

  private static string DecodeName(ReadOnlySpan<byte> field) {
    var end = field.IndexOf((byte)0);
    var raw = end < 0 ? field : field[..end];
    foreach (var b in raw)
      if (b >= 0x80) return ShiftJis().GetString(raw);
    return Encoding.ASCII.GetString(raw);
  }

  private static Encoding ShiftJis() {
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    return Encoding.GetEncoding(932);
  }

  /// <summary>Expands a byte pair encoded stream to <paramref name="size"/> bytes.</summary>
  /// <remarks>
  /// Each block starts with its pair table. Every byte value is a literal unless the table makes it
  /// stand for a pair. The table is written as runs: a count byte c ≤ 127 introduces c + 1
  /// consecutive codes, and a count byte above 127 first skips c − 127 codes that stay literal and
  /// then introduces one. Each introduced code carries its left byte, and a right byte too unless
  /// the left byte equals the code, which keeps it literal. The table ends when the codes reach
  /// 256. The packed data follows; codes expand recursively.
  /// </remarks>
  internal static byte[] ExpandBytePairs(ReadOnlySpan<byte> input, long size) {
    if (size > int.MaxValue) throw new InvalidDataException("Quadruple D file is too large.");
    if (size == 0) return [];
    var output = new byte[size];
    var produced = 0;
    var p = 0;
    Span<byte> left = stackalloc byte[256];
    Span<byte> right = stackalloc byte[256];
    Span<byte> stack = stackalloc byte[512];

    while (produced < size) {
      if (p >= input.Length) throw new InvalidDataException("Quadruple D data ends early.");
      for (var i = 0; i < 256; ++i) left[i] = (byte)i;

      var code = 0;
      while (code < 256) {
        int count = Next(input, ref p);
        if (count > 127) {
          code += count - 127;
          count = 0;
        }
        if (code > 256) throw new InvalidDataException("Quadruple D pair table is corrupt.");
        if (code == 256) break;
        for (var i = 0; i <= count && code < 256; ++i, ++code) {
          left[code] = Next(input, ref p);
          if (left[code] != code) right[code] = Next(input, ref p);
        }
      }

      if (p + 4 > input.Length) throw new InvalidDataException("Quadruple D data ends early.");
      var packed = BinaryPrimitives.ReadUInt32LittleEndian(input[p..]);
      p += 4;
      if (packed > input.Length - p) throw new InvalidDataException("Quadruple D block runs past its file.");
      var end = p + (int)packed;

      while (p < end) {
        var depth = 0;
        stack[depth++] = input[p++];
        while (depth > 0) {
          var c = stack[--depth];
          if (left[c] == c) {
            if (produced >= size) throw new InvalidDataException("Quadruple D file decodes past its size.");
            output[produced++] = c;
          } else {
            if (depth + 2 > stack.Length) throw new InvalidDataException("Quadruple D pair table is cyclic.");
            stack[depth++] = right[c];
            stack[depth++] = left[c];
          }
        }
      }
    }
    if (p != input.Length) throw new InvalidDataException("Quadruple D file has data after its last block.");
    return output;
  }

  private static byte Next(ReadOnlySpan<byte> input, ref int p)
    => p < input.Length ? input[p++] : throw new InvalidDataException("Quadruple D data ends early.");
}
