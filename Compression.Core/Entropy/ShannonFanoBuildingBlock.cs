using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes Shannon-Fano coding as a benchmarkable building block.
/// </summary>
public sealed class ShannonFanoBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_ShannonFano";
  /// <inheritdoc/>
  public string DisplayName => "Shannon-Fano";
  /// <inheritdoc/>
  public string Description => "Historical predecessor to Huffman, recursive frequency splitting";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write original size.
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    // Count frequencies.
    var freq = new int[256];
    foreach (var b in data)
      freq[b]++;

    // Write frequency table (256 x 2-byte LE).
    var maxFreq = 0;
    foreach (var f in freq)
      if (f > maxFreq) maxFreq = f;

    var scaledFreq = new ushort[256];
    if (maxFreq > ushort.MaxValue) {
      for (var i = 0; i < 256; i++) {
        if (freq[i] > 0)
          scaledFreq[i] = (ushort)Math.Max(1, (int)((long)freq[i] * ushort.MaxValue / maxFreq));
      }
    } else {
      for (var i = 0; i < 256; i++)
        scaledFreq[i] = (ushort)freq[i];
    }

    Span<byte> freqBuf = stackalloc byte[512];
    for (var i = 0; i < 256; i++)
      BinaryPrimitives.WriteUInt16LittleEndian(freqBuf.Slice(i * 2, 2), scaledFreq[i]);
    ms.Write(freqBuf);

    if (data.Length == 0)
      return ms.ToArray();

    // Build codes from original (unscaled) frequencies for encoding.
    var codes = new (uint code, int length)[256];
    BuildCodes(freq, codes);

    // Write packed bitstream.
    var writer = new BitWriter(ms);
    foreach (var b in data) {
      var (code, length) = codes[b];
      writer.WriteBits(code, length);
    }
    writer.Flush();

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var offset = 0;

    // Read original size.
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    offset += 4;

    if (originalSize == 0)
      return [];

    // Read frequency table.
    var freq = new int[256];
    for (var i = 0; i < 256; i++) {
      freq[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
      offset += 2;
    }

    // Build tree from frequencies.
    var root = BuildTree(freq);

    // Read remaining bytes as bitstream.
    var bitData = data[offset..].ToArray();

    // Decode bitstream.
    var result = new byte[originalSize];
    var bitIndex = 0;
    for (var i = 0; i < originalSize; i++) {
      var node = root;
      while (node!.Left != null || node.Right != null) {
        if (bitIndex / 8 >= bitData.Length)
          throw new InvalidDataException("Unexpected end of Shannon-Fano bitstream.");
        var bit = (bitData[bitIndex / 8] >> (7 - (bitIndex % 8))) & 1;
        bitIndex++;
        node = bit == 0 ? node.Left : node.Right;
        if (node == null)
          throw new InvalidDataException("Invalid Shannon-Fano bitstream.");
      }
      result[i] = node.Symbol;
    }

    return result;
  }

  private static void BuildCodes(int[] freq, (uint code, int length)[] codes) {
    var symbols = new List<(byte symbol, int freq)>();
    for (var i = 0; i < 256; i++) {
      if (freq[i] > 0)
        symbols.Add(((byte)i, freq[i]));
    }

    if (symbols.Count == 0)
      return;

    if (symbols.Count == 1) {
      codes[symbols[0].symbol] = (0, 1);
      return;
    }

    symbols.Sort((a, b) => a.freq != b.freq ? b.freq.CompareTo(a.freq) : a.symbol.CompareTo(b.symbol));
    AssignCodes(symbols, codes, 0, 0);
  }

  private static void AssignCodes(List<(byte symbol, int freq)> symbols,
    (uint code, int length)[] codes, uint currentCode, int depth) {
    if (symbols.Count == 1) {
      codes[symbols[0].symbol] = (currentCode, Math.Max(1, depth));
      return;
    }

    if (symbols.Count == 0)
      return;

    long total = 0;
    foreach (var (_, f) in symbols)
      total += f;

    long runningSum = 0;
    var splitIndex = 0;
    var minDiff = long.MaxValue;
    for (var i = 0; i < symbols.Count - 1; i++) {
      runningSum += symbols[i].freq;
      var diff = Math.Abs(2 * runningSum - total);
      if (diff < minDiff) {
        minDiff = diff;
        splitIndex = i + 1;
      }
    }

    var left = symbols.GetRange(0, splitIndex);
    var right = symbols.GetRange(splitIndex, symbols.Count - splitIndex);

    AssignCodes(left, codes, currentCode << 1, depth + 1);
    AssignCodes(right, codes, (currentCode << 1) | 1, depth + 1);
  }

  private static SfNode BuildTree(int[] freq) {
    var symbols = new List<(byte symbol, int freq)>();
    for (var i = 0; i < 256; i++) {
      if (freq[i] > 0)
        symbols.Add(((byte)i, freq[i]));
    }

    if (symbols.Count == 0)
      return new SfNode { Symbol = 0 };

    if (symbols.Count == 1)
      return BuildSingleSymbolTree(symbols[0].symbol);

    symbols.Sort((a, b) => a.freq != b.freq ? b.freq.CompareTo(a.freq) : a.symbol.CompareTo(b.symbol));
    return BuildSubTree(symbols);
  }

  private static SfNode BuildSingleSymbolTree(byte symbol) {
    return new SfNode {
      Left = new SfNode { Symbol = symbol },
      Right = new SfNode { Symbol = symbol }
    };
  }

  private static SfNode BuildSubTree(List<(byte symbol, int freq)> symbols) {
    if (symbols.Count == 1)
      return new SfNode { Symbol = symbols[0].symbol };

    long total = 0;
    foreach (var (_, f) in symbols)
      total += f;

    long runningSum = 0;
    var splitIndex = 0;
    var minDiff = long.MaxValue;
    for (var i = 0; i < symbols.Count - 1; i++) {
      runningSum += symbols[i].freq;
      var diff = Math.Abs(2 * runningSum - total);
      if (diff < minDiff) {
        minDiff = diff;
        splitIndex = i + 1;
      }
    }

    var left = symbols.GetRange(0, splitIndex);
    var right = symbols.GetRange(splitIndex, symbols.Count - splitIndex);

    return new SfNode {
      Left = BuildSubTree(left),
      Right = BuildSubTree(right)
    };
  }

  private sealed class SfNode {
    public byte Symbol;
    public SfNode? Left;
    public SfNode? Right;
  }

  private sealed class BitWriter(Stream output) {
    private byte _buffer;
    private int _bitCount;

    public void WriteBits(uint value, int count) {
      for (var i = count - 1; i >= 0; i--) {
        _buffer = (byte)(((uint)(_buffer << 1) & 0xFF) | ((value >> i) & 1));
        _bitCount++;
        if (_bitCount == 8) {
          output.WriteByte(_buffer);
          _buffer = 0;
          _bitCount = 0;
        }
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
