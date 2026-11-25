#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Checksums;

namespace FileFormat.ExePackers;

/// <summary>
/// DIET-compressed data files (Teddy Matsumoto's DIET, non-executable files). Three
/// layouts exist; all carry the same LZ77 stream <see cref="DietUnpacker"/> decodes:
/// <list type="bullet">
/// <item>1.44-1.45f: <c>B4 4C CD 21 9D 89</c>, then the 11-byte <c>dlz</c> header, data at 17;</item>
/// <item>1.02b-1.20: <c>9D 89</c>, then the <c>dlz</c> header, data at 13;</item>
/// <item>1.00: <c>B4 4C CD 21 9D 89</c>, a CRC-16 of the compressed data, data at 8 to the end.</item>
/// </list>
/// The <c>dlz</c> header holds the flags and compressed length (4 + 16 bits), the CRC-16/ARC of
/// the compressed bytes and the original length (6 + 16 bits). <see cref="Encode"/> writes the
/// 1.44-1.45f layout, which DIET 1.45f restores with <c>DIET -R</c>.
/// </summary>
public static class DietDataCodec {
  private static ReadOnlySpan<byte> Int21 => [0xB4, 0x4C, 0xCD, 0x21];
  private static ReadOnlySpan<byte> Sig9D89 => [0x9D, 0x89];
  private static ReadOnlySpan<byte> Dlz => "dlz"u8;

  /// <summary>Largest original length the 22-bit field can carry.</summary>
  public const int MaximumOriginalLength = (1 << 22) - 1;

  /// <summary>Largest compressed length the 20-bit field can carry.</summary>
  public const int MaximumCompressedLength = (1 << 20) - 1;

  private const int WindowSize = 8192;
  private const int MaximumMatch = 272;

  /// <summary>Which layout <paramref name="file"/> has, or <see langword="null"/>.</summary>
  public static string? Identify(ReadOnlySpan<byte> file) {
    if (file.Length >= 17 && file[..4].SequenceEqual(Int21) && file[4..6].SequenceEqual(Sig9D89) && file[6..9].SequenceEqual(Dlz))
      return "1.44-1.45";
    if (file.Length >= 13 && file[..2].SequenceEqual(Sig9D89) && file[2..5].SequenceEqual(Dlz))
      return "1.02-1.20";
    if (file.Length >= 8 && file[..4].SequenceEqual(Int21) && file[4..6].SequenceEqual(Sig9D89))
      return "1.00";
    return null;
  }

  /// <summary>
  /// Restores a DIET data file. The compressed length, the CRC-16 of the compressed bytes and,
  /// from 1.02 on, the original length are checked; any mismatch throws
  /// <see cref="InvalidDataException"/>.
  /// </summary>
  public static byte[] Decode(ReadOnlySpan<byte> file) {
    var layout = Identify(file) ?? throw new InvalidDataException("DIET data: no DIET data header.");
    int dataPos;
    long compressedLength, originalLength = -1;
    ushort crc;
    if (layout == "1.00") {
      dataPos = 8;
      crc = BinaryPrimitives.ReadUInt16LittleEndian(file[6..]);
      compressedLength = file.Length - dataPos;
    } else {
      var dlz = layout == "1.44-1.45" ? 6 : 2;
      dataPos = dlz + 11;
      var x = file[dlz + 3];
      if ((x & 0x80) != 0)
        throw new InvalidDataException("DIET data: multi-block files are not supported.");
      compressedLength = ((long)(x & 0x0F) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(file[(dlz + 4)..]);
      crc = BinaryPrimitives.ReadUInt16LittleEndian(file[(dlz + 6)..]);
      originalLength = ((long)(file[dlz + 8] & 0xFC) << 14) | BinaryPrimitives.ReadUInt16LittleEndian(file[(dlz + 9)..]);
    }

    if (dataPos + compressedLength > file.Length)
      throw new InvalidDataException("DIET data: compressed data runs past the end of the file.");
    var compressed = file.Slice(dataPos, (int)compressedLength);
    if (Crc16.Compute(compressed) != crc)
      throw new InvalidDataException("DIET data: CRC-16 of the compressed data does not match.");

    // The decoder refills its bit word right after the stop code, so it is given the bytes to the
    // end of the file rather than exactly the recorded length.
    var data = DietUnpacker.DecompressStream(file[dataPos..], isExe: false);
    if (originalLength >= 0 && data.Length != originalLength)
      throw new InvalidDataException($"DIET data: {data.Length} bytes decoded where the header records {originalLength}.");
    return data;
  }

  /// <summary>Compresses <paramref name="data"/> into a DIET 1.44-1.45f data file.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length > MaximumOriginalLength)
      throw new ArgumentException($"DIET data files hold at most {MaximumOriginalLength} bytes.", nameof(data));

    var stream = EncodeStream(data);
    if (stream.Length > MaximumCompressedLength)
      throw new ArgumentException("DIET data: the compressed stream exceeds the 20-bit length field.", nameof(data));

    var file = new byte[17 + stream.Length];
    Int21.CopyTo(file);
    Sig9D89.CopyTo(file.AsSpan(4));
    Dlz.CopyTo(file.AsSpan(6));
    file[9] = (byte)((stream.Length >> 16) & 0x0F);
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(10), (ushort)stream.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12), Crc16.Compute(stream));
    file[14] = (byte)((data.Length >> 14) & 0xFC);
    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(15), (ushort)data.Length);
    stream.CopyTo(file.AsSpan(17));
    return file;
  }

  /// <summary>
  /// The LZ77 stream: flag bits LSB-first in 16-bit little-endian words that are placed in the
  /// output the moment the previous word fills up, interleaved with literal and distance bytes.
  /// Matches never overlap their own output (length at most the back distance).
  /// </summary>
  internal static byte[] EncodeStream(ReadOnlySpan<byte> data) {
    var w = new BitWriter();
    const int HashSize = 1 << 15;
    var head = new int[HashSize];
    var prev = new int[WindowSize];
    var last2 = new int[1 << 16];
    Array.Fill(head, -1);
    Array.Fill(last2, -1);

    static int Hash(ReadOnlySpan<byte> d, int i) => ((d[i] << 10) ^ (d[i + 1] << 5) ^ d[i + 2]) & (HashSize - 1);

    void Insert(ReadOnlySpan<byte> d, int i) {
      if (i + 1 < d.Length)
        last2[(d[i] << 8) | d[i + 1]] = i;
      if (i + 2 >= d.Length)
        return;
      var h = Hash(d, i);
      prev[i & (WindowSize - 1)] = head[h];
      head[h] = i;
    }

    (int Length, int Distance) Find(ReadOnlySpan<byte> d, int i) {
      var bestLength = 0;
      var bestDistance = 0;
      // Length-2 matches: the latest earlier occurrence of the pair, when one of the two
      // length-2 codes reaches it (back distance 2..2304; distance 0 is the stop code's byte).
      if (i + 1 < d.Length && last2[(d[i] << 8) | d[i + 1]] is var before and >= 0) {
        var back = i - before;
        if (back is >= 2 and <= 2304) {
          bestLength = 2;
          bestDistance = back - 1;
        }
      }
      if (i + 2 >= d.Length)
        return (bestLength, bestDistance);

      var chain = 512;
      for (var candidate = head[Hash(d, i)]; candidate >= 0 && i - candidate <= WindowSize && chain-- > 0; candidate = prev[candidate & (WindowSize - 1)]) {
        var back = i - candidate;
        var limit = Math.Min(Math.Min(MaximumMatch, d.Length - i), back);
        if (limit <= bestLength || d[candidate + bestLength] != d[i + bestLength])
          continue;
        var length = 0;
        while (length < limit && d[candidate + length] == d[i + length])
          ++length;
        if (length >= 3 && length > bestLength) {
          bestLength = length;
          bestDistance = back - 1;
          if (length == limit && limit == MaximumMatch)
            break;
        }
      }

      return (bestLength, bestDistance);
    }

    var pos = 0;
    while (pos < data.Length) {
      var (length, distance) = Find(data, pos);
      if (length >= 3 && pos + 1 < data.Length) {
        // One step of lazy evaluation: take a literal when the next position matches longer.
        Insert(data, pos);
        var (nextLength, _) = Find(data, pos + 1);
        if (nextLength > length + 1) {
          w.Bit(1);
          w.Byte(data[pos]);
          ++pos;
          continue;
        }
        WriteMatch(w, length, distance);
        for (var k = 1; k < length; ++k)
          Insert(data, pos + k);
        pos += length;
        continue;
      }

      if (length == 2) {
        WriteMatch(w, 2, distance);
        Insert(data, pos);
        Insert(data, pos + 1);
        pos += 2;
        continue;
      }

      Insert(data, pos);
      w.Bit(1);
      w.Byte(data[pos]);
      ++pos;
    }

    // Stop code: 0, 0, byte 0xFF, 0, 0.
    w.Bit(0);
    w.Bit(0);
    w.Byte(0xFF);
    w.Bit(0);
    w.Bit(0);
    return w.Finish();
  }

  private static void WriteMatch(BitWriter w, int length, int distance) {
    w.Bit(0);
    if (length == 2) {
      w.Bit(0);
      if (distance <= 255) {
        w.Byte((byte)(0xFF - distance));
        w.Bit(0);
      } else {
        var code = 2303 - distance; // 1024*a2 + 512*a3 + 256*a4 + v
        w.Byte((byte)code);
        w.Bit(1);
        w.Bit((code >> 10) & 1);
        w.Bit((code >> 9) & 1);
        w.Bit((code >> 8) & 1);
      }
      return;
    }

    w.Bit(1);
    if (distance <= 511) {
      var code = 511 - distance;
      w.Byte((byte)code);
      w.Bit((code >> 8) & 1);
      w.Bit(1);
    } else if (distance <= 1023) {
      var code = 1023 - distance;
      w.Byte((byte)code);
      w.Bit((code >> 8) & 1);
      w.Bit(0);
      w.Bit(1);
    } else if (distance <= 2047) {
      var code = 2047 - distance;
      w.Byte((byte)code);
      w.Bit((code >> 9) & 1);
      w.Bit(0);
      w.Bit(0);
      w.Bit((code >> 8) & 1);
      w.Bit(1);
    } else if (distance <= 4095) {
      var code = 4095 - distance;
      w.Byte((byte)code);
      w.Bit((code >> 10) & 1);
      w.Bit(0);
      w.Bit(0);
      w.Bit((code >> 9) & 1);
      w.Bit(0);
      w.Bit((code >> 8) & 1);
      w.Bit(1);
    } else {
      var code = 8191 - distance;
      w.Byte((byte)code);
      w.Bit((code >> 11) & 1);
      w.Bit(0);
      w.Bit(0);
      w.Bit((code >> 10) & 1);
      w.Bit(0);
      w.Bit((code >> 9) & 1);
      w.Bit(0);
      w.Bit((code >> 8) & 1);
    }

    // Length: 3..6 as (n-1) zeros then a one; 7..8 as 0000 1 x; 9..16 as 0000 00 xxx; 17..272 as 0000 01 byte.
    if (length <= 6) {
      for (var k = 3; k < length; ++k)
        w.Bit(0);
      w.Bit(1);
      return;
    }
    for (var k = 0; k < 4; ++k)
      w.Bit(0);
    if (length <= 8) {
      w.Bit(1);
      w.Bit(length - 7);
    } else if (length <= 16) {
      w.Bit(0);
      w.Bit(0);
      var v = length - 9;
      w.Bit((v >> 2) & 1);
      w.Bit((v >> 1) & 1);
      w.Bit(v & 1);
    } else {
      w.Bit(0);
      w.Bit(1);
      w.Byte((byte)(length - 17));
    }
  }

  /// <summary>
  /// Reproduces the decoder's word discipline: a word slot is reserved at the current end of
  /// the output whenever the previous one has taken its sixteenth bit (and once at the start).
  /// </summary>
  private sealed class BitWriter {
    private readonly List<byte> output = [];
    private int wordPos;
    private int count;
    private uint word;

    public BitWriter() => this.Reserve();

    private void Reserve() {
      this.wordPos = this.output.Count;
      this.output.Add(0);
      this.output.Add(0);
      this.word = 0;
      this.count = 0;
    }

    public void Bit(int b) {
      this.word |= (uint)(b & 1) << this.count;
      if (++this.count < 16)
        return;
      this.output[this.wordPos] = (byte)this.word;
      this.output[this.wordPos + 1] = (byte)(this.word >> 8);
      this.Reserve();
    }

    public void Byte(byte b) => this.output.Add(b);

    public byte[] Finish() {
      this.output[this.wordPos] = (byte)this.word;
      this.output[this.wordPos + 1] = (byte)(this.word >> 8);
      return [.. this.output];
    }
  }
}
