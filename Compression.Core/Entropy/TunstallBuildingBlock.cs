using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes Tunstall coding as a benchmarkable building block.
/// Variable-to-fixed-length code (dual of Huffman): builds a dictionary of variable-length
/// input phrases, each assigned a fixed-width codeword. The dictionary is built by
/// repeatedly extending the highest-probability leaf.
/// </summary>
public sealed class TunstallBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Tunstall";
  /// <inheritdoc/>
  public string DisplayName => "Tunstall Coding";
  /// <inheritdoc/>
  public string Description => "Variable-to-fixed code, dual of Huffman, dictionary of input phrases";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  // Fixed-width codeword size: 12 bits → max 4096 dictionary entries.
  private const int CodeBits = 12;
  private const int MaxEntries = 1 << CodeBits;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write 4-byte LE uncompressed size.
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    // Build frequency table.
    Span<int> freq = stackalloc int[256];
    foreach (var b in data)
      freq[b]++;

    // Compute probabilities.
    var prob = new double[256];
    for (var i = 0; i < 256; i++)
      prob[i] = (double)freq[i] / data.Length;

    // Write frequency table (2 bytes per symbol, big enough for our purposes).
    Span<byte> freqBuf = stackalloc byte[4];
    for (var i = 0; i < 256; i++) {
      BinaryPrimitives.WriteInt32LittleEndian(freqBuf, freq[i]);
      ms.Write(freqBuf);
    }

    // Build Tunstall dictionary.
    var dictionary = BuildDictionary(prob);

    // Encode: greedily match longest dictionary phrase.
    var writer = new BitWriter(ms);
    var pos = 0;
    while (pos < data.Length) {
      var bestCode = -1;
      var bestLen = 0;

      // Linear scan for longest matching phrase.
      for (var d = 0; d < dictionary.Count; d++) {
        var phrase = dictionary[d];
        if (phrase.Length <= bestLen || pos + phrase.Length > data.Length)
          continue;

        var match = true;
        for (var j = 0; j < phrase.Length; j++) {
          if (data[pos + j] != phrase[j]) {
            match = false;
            break;
          }
        }

        if (match) {
          bestCode = d;
          bestLen = phrase.Length;
        }
      }

      if (bestCode < 0) {
        // Fallback: single-byte entry must always exist.
        bestCode = data[pos];
        bestLen = 1;
      }

      // Write fixed-width codeword.
      for (var i = CodeBits - 1; i >= 0; i--)
        writer.WriteBit((bestCode >> i) & 1);

      pos += bestLen;
    }
    writer.Flush();

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    var offset = 4;

    // Read frequency table.
    var freq = new int[256];
    for (var i = 0; i < 256; i++) {
      freq[i] = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
      offset += 4;
    }

    // Compute probabilities from frequencies.
    var total = 0L;
    for (var i = 0; i < 256; i++)
      total += freq[i];

    var prob = new double[256];
    if (total > 0)
      for (var i = 0; i < 256; i++)
        prob[i] = (double)freq[i] / total;

    // Rebuild dictionary (deterministic from probabilities).
    var dictionary = BuildDictionary(prob);

    // Decode fixed-width codewords.
    var src = data[offset..].ToArray();
    var bitIndex = 0;
    var result = new List<byte>(originalSize);

    while (result.Count < originalSize) {
      // Read CodeBits-wide codeword.
      var code = 0;
      for (var i = 0; i < CodeBits; i++)
        code = (code << 1) | ReadBit(src, ref bitIndex);

      if (code >= dictionary.Count)
        throw new InvalidDataException($"Tunstall codeword {code} exceeds dictionary size {dictionary.Count}.");

      var phrase = dictionary[code];
      for (var j = 0; j < phrase.Length && result.Count < originalSize; j++)
        result.Add(phrase[j]);
    }

    return [.. result];
  }

  private static List<byte[]> BuildDictionary(double[] prob) {
    // Start with 256 single-byte phrases (one per symbol).
    var entries = new List<(byte[] Phrase, double Prob)>();
    for (var i = 0; i < 256; i++)
      entries.Add(([( byte)i], prob[i]));

    // Extend highest-probability leaf until we reach MaxEntries.
    while (entries.Count + 255 <= MaxEntries) {
      // Find highest-probability leaf.
      var bestIdx = 0;
      var bestProb = entries[0].Prob;
      for (var i = 1; i < entries.Count; i++) {
        if (entries[i].Prob > bestProb) {
          bestProb = entries[i].Prob;
          bestIdx = i;
        }
      }

      if (bestProb <= 0)
        break;

      // Replace the leaf with 256 children (leaf + each possible next byte).
      var parent = entries[bestIdx];
      entries.RemoveAt(bestIdx);

      for (var c = 0; c < 256; c++) {
        var child = new byte[parent.Phrase.Length + 1];
        parent.Phrase.CopyTo(child, 0);
        child[^1] = (byte)c;
        entries.Add((child, parent.Prob * prob[c]));
      }
    }

    // Ensure all 256 single-byte entries exist (splitting may have removed some).
    var hasSingleByte = new bool[256];
    foreach (var e in entries)
      if (e.Phrase.Length == 1)
        hasSingleByte[e.Phrase[0]] = true;
    for (var i = 0; i < 256; i++)
      if (!hasSingleByte[i])
        entries.Add(([(byte)i], prob[i]));

    // Sort by phrase for deterministic ordering: lexicographic on (length, content).
    entries.Sort((a, b) => {
      var lenCmp = a.Phrase.Length.CompareTo(b.Phrase.Length);
      if (lenCmp != 0) return lenCmp;
      for (var i = 0; i < a.Phrase.Length; i++) {
        var cmp = a.Phrase[i].CompareTo(b.Phrase[i]);
        if (cmp != 0) return cmp;
      }
      return 0;
    });

    return entries.Select(e => e.Phrase).ToList();
  }

  private static int ReadBit(byte[] data, ref int bitIndex) {
    if (bitIndex / 8 >= data.Length)
      throw new InvalidDataException("Unexpected end of Tunstall bitstream.");
    var bit = (data[bitIndex / 8] >> (7 - (bitIndex % 8))) & 1;
    bitIndex++;
    return bit;
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
