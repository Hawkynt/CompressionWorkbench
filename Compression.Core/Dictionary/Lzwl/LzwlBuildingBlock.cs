using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzwl;

/// <summary>
/// Exposes LZWL as a benchmarkable building block.
/// LZW with variable-length alphabet symbols: the initial dictionary is extended with
/// frequent digram pairs found via frequency analysis, allowing faster convergence.
/// Uses a trie (parent,child) structure like standard LZW, with a stop code for clean
/// end-of-stream signaling.
/// </summary>
public sealed class LzwlBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzwl";
  /// <inheritdoc/>
  public string DisplayName => "LZWL";
  /// <inheritdoc/>
  public string Description => "LZW with variable-length initial alphabet from digram analysis";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int MaxBits = 16;
  private const int MaxDictSize = 1 << MaxBits;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write 4-byte LE uncompressed size.
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    // Analyze digram frequencies.
    var digramFreq = new int[256 * 256];
    for (var i = 0; i < data.Length - 1; i++)
      digramFreq[(data[i] << 8) | data[i + 1]]++;

    // Select top 128 digrams that appear >= 2 times.
    var topDigrams = new List<int>();
    for (var d = 0; d < 65536; d++) {
      if (digramFreq[d] >= 2)
        topDigrams.Add(d);
    }
    topDigrams.Sort((a, b) => digramFreq[b].CompareTo(digramFreq[a]));
    if (topDigrams.Count > 128)
      topDigrams.RemoveRange(128, topDigrams.Count - 128);

    // Write digram table.
    Span<byte> buf2 = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(buf2, (ushort)topDigrams.Count);
    ms.Write(buf2);
    foreach (var d in topDigrams) {
      ms.WriteByte((byte)(d >> 8));
      ms.WriteByte((byte)(d & 0xFF));
    }

    // Build initial dictionary as a trie: (parentCode, childByte) → code.
    // Codes 0-255 = single bytes.
    // Codes 256..256+N-1 = digrams.
    // Code 256+N = stop code.
    var trie = new Dictionary<(int Parent, byte Child), int>();
    var trieNextCode = 256;

    // Pre-populate trie with digram entries.
    foreach (var d in topDigrams) {
      var a = (byte)(d >> 8);
      var b = (byte)(d & 0xFF);
      var key = (Parent: (int)a, Child: b);
      if (!trie.ContainsKey(key))
        trie[key] = trieNextCode;
      trieNextCode++;
    }

    var stopCode = trieNextCode++;
    var decoderNextCode = trieNextCode; // First code available for new entries.
    var hasPrevious = false;

    // Initial code width.
    var codeWidth = 9;
    while ((1 << codeWidth) < trieNextCode)
      codeWidth++;

    // LZW encode using trie.
    var writer = new BitWriter(ms);
    var currentCode = (int)data[0];
    var i2 = 1;

    while (i2 < data.Length) {
      var nextByte = data[i2];
      var key = (Parent: currentCode, Child: nextByte);

      if (trie.TryGetValue(key, out var existingCode)) {
        currentCode = existingCode;
        i2++;
      } else {
        // Output current code.
        WriteBits(writer, currentCode, codeWidth);

        // Add new trie entry.
        if (trieNextCode < MaxDictSize) {
          trie[key] = trieNextCode;
          trieNextCode++;
        }

        // Mirror decoder: only bump after decoder has a previous entry.
        if (hasPrevious) {
          if (decoderNextCode < MaxDictSize) {
            decoderNextCode++;
            if (decoderNextCode >= (1 << codeWidth) && codeWidth < MaxBits)
              codeWidth++;
          }
        }
        hasPrevious = true;

        currentCode = nextByte;
        i2++;
      }
    }

    // Output final code.
    WriteBits(writer, currentCode, codeWidth);

    // Decoder adds one more entry after the final data code.
    if (hasPrevious && decoderNextCode < MaxDictSize) {
      decoderNextCode++;
      if (decoderNextCode >= (1 << codeWidth) && codeWidth < MaxBits)
        codeWidth++;
    }

    // Write stop code.
    WriteBits(writer, stopCode, codeWidth);

    writer.Flush();
    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    var offset = 4;

    // Read digram table.
    var digramCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    offset += 2;

    // Build initial dictionary: code → byte sequence.
    var dict = new Dictionary<int, byte[]>();
    var nextCode = 0;

    for (var i = 0; i < 256; i++)
      dict[nextCode++] = [(byte)i];

    for (var i = 0; i < digramCount; i++) {
      var a = data[offset++];
      var b = data[offset++];
      dict[nextCode++] = [a, b];
    }

    var stopCode = nextCode++;

    var codeWidth = 9;
    while ((1 << codeWidth) < nextCode)
      codeWidth++;

    // LZW decode.
    var src = data[offset..].ToArray();
    var bitIndex = 0;
    var result = new List<byte>(originalSize);

    var firstCode = ReadBits(src, ref bitIndex, codeWidth);
    if (firstCode == stopCode || !dict.TryGetValue(firstCode, out var prevEntry))
      return [.. result];
    result.AddRange(prevEntry);

    while (result.Count < originalSize) {
      var code = ReadBits(src, ref bitIndex, codeWidth);
      if (code == stopCode)
        break;

      byte[] entry;
      if (dict.TryGetValue(code, out var existing)) {
        entry = existing;
      } else if (code == nextCode) {
        // KwKwK case.
        entry = new byte[prevEntry.Length + 1];
        prevEntry.CopyTo(entry, 0);
        entry[^1] = prevEntry[0];
      } else {
        throw new InvalidDataException($"LZWL: unknown code {code} at position {result.Count}.");
      }

      result.AddRange(entry);

      // Add new dictionary entry.
      if (nextCode < MaxDictSize) {
        var newEntry = new byte[prevEntry.Length + 1];
        prevEntry.CopyTo(newEntry, 0);
        newEntry[^1] = entry[0];
        dict[nextCode++] = newEntry;
        if (nextCode >= (1 << codeWidth) && codeWidth < MaxBits)
          codeWidth++;
      }

      prevEntry = entry;
    }

    if (result.Count > originalSize)
      result.RemoveRange(originalSize, result.Count - originalSize);

    return [.. result];
  }

  private static void WriteBits(BitWriter writer, int value, int count) {
    for (var i = count - 1; i >= 0; i--)
      writer.WriteBit((value >> i) & 1);
  }

  private static int ReadBits(byte[] data, ref int bitIndex, int count) {
    var value = 0;
    for (var i = 0; i < count; i++) {
      if (bitIndex / 8 >= data.Length)
        throw new InvalidDataException("Unexpected end of LZWL bitstream.");
      var bit = (data[bitIndex / 8] >> (7 - (bitIndex % 8))) & 1;
      bitIndex++;
      value = (value << 1) | bit;
    }
    return value;
  }

  private sealed class BitWriter(Stream output) {
    private byte _buffer;
    private int _bitCount;

    public void WriteBit(int bit) {
      _buffer = (byte)((_buffer << 1) | (bit & 1));
      _bitCount++;
      if (_bitCount == 8) {
        output.WriteByte(_buffer);
        _buffer = 0;
        _bitCount = 0;
      }
    }

    public void Flush() {
      if (_bitCount > 0) {
        _buffer <<= (8 - _bitCount);
        output.WriteByte(_buffer);
        _buffer = 0;
        _bitCount = 0;
      }
    }
  }
}
