namespace Compression.Core.Dictionary.Ace;

/// <summary>
/// Writes ACE 1.0 LZ77 streams (compression type 1) that <see cref="AceDecompressor"/> and the
/// reference decoders read: literals and explicit-distance matches over two block-wise Huffman
/// trees. One instance per archive; with <c>solid</c> the dictionary carries over between members
/// exactly as the decoder's does.
/// </summary>
/// <remarks>
/// The bit stream, tree serialisation and code assignment mirror the decoder (see
/// <see cref="AceHuffman"/>); the match search is our own greedy hash-chain parser. The
/// repeat-distance codes 256..259 are not emitted.
/// </remarks>
public sealed class AceCompressor {
  private const int HashBits = 16;
  private const int MaxChain = 96;
  private const int MaxBlockSymbols = 32767;

  private readonly int _dictBits;
  private readonly bool _solid;
  private byte[] _history = [];

  /// <summary>Creates a compressor with a dictionary of 2^<paramref name="dictionaryBits"/> bytes.</summary>
  public AceCompressor(int dictionaryBits = AceConstants.DefaultDictBits, bool solid = false) {
    this._dictBits = Math.Clamp(dictionaryBits, AceConstants.MinDictBits, AceConstants.MaxDictBits);
    this._solid = solid;
  }

  /// <summary>Dictionary size in bits (what the file header's parameter field records).</summary>
  public int DictionaryBits => this._dictBits;

  /// <summary>
  /// Tells a solid compressor about a member that was stored rather than compressed: the decoder adds
  /// stored bytes to its dictionary too.
  /// </summary>
  public void RegisterStored(ReadOnlySpan<byte> data) {
    if (this._solid)
      this.Remember(data);
  }

  private void Remember(ReadOnlySpan<byte> data) {
    var keep = 1 << this._dictBits;
    var combined = new byte[Math.Min(this._history.Length + data.Length, keep)];
    var fromData = Math.Min(data.Length, combined.Length);
    var fromHistory = combined.Length - fromData;
    this._history.AsSpan(this._history.Length - fromHistory).CopyTo(combined);
    data[(data.Length - fromData)..].CopyTo(combined.AsSpan(fromHistory));
    this._history = combined;
  }

  private static int BitLength(uint v) => v == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount(v);

  private static int MinLengthFor(int dist) => dist - 1 <= AceConstants.MaxDistAtLen2 ? 2 : dist - 1 <= AceConstants.MaxDistAtLen3 ? 3 : 4;

  /// <summary>Compresses one member.</summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      return [];
    var history = this._solid ? this._history : [];
    var buffer = new byte[history.Length + data.Length];
    history.CopyTo(buffer, 0);
    data.CopyTo(buffer.AsSpan(history.Length));
    var start = history.Length;
    var maxDist = (1 << this._dictBits) - 1;

    // Tokens: literal (Length == 0) or match (Length, Distance).
    var tokens = new List<(int Length, int Distance, byte Literal)>();
    var head = new int[1 << HashBits];
    Array.Fill(head, -1);
    var prev = new int[buffer.Length];

    static int Hash(byte[] b, int i) => ((b[i] << 10) ^ (b[i + 1] << 5) ^ b[i + 2]) & ((1 << HashBits) - 1);

    void Insert(int i) {
      if (i + 2 >= buffer.Length)
        return;
      var h = Hash(buffer, i);
      prev[i] = head[h];
      head[h] = i;
    }

    for (var i = 0; i < start; ++i)
      Insert(i);

    var pos = start;
    while (pos < buffer.Length) {
      var remaining = buffer.Length - pos;
      int bestLen = 0, bestDist = 0;
      if (remaining >= 3) {
        var candidate = head[Hash(buffer, pos)];
        var chain = 0;
        while (candidate >= 0 && chain++ < MaxChain) {
          var dist = pos - candidate;
          if (dist > maxDist)
            break;
          var minLen = MinLengthFor(dist);
          var limit = Math.Min(remaining, 254 + minLen);
          var len = 0;
          while (len < limit && buffer[candidate + len] == buffer[pos + len])
            ++len;
          if (len >= minLen && len > bestLen) {
            bestLen = len;
            bestDist = dist;
            if (len == limit)
              break;
          }
          candidate = prev[candidate];
        }
      }
      if (bestLen > 0) {
        tokens.Add((bestLen, bestDist, 0));
        for (var k = 0; k < bestLen; ++k)
          Insert(pos + k);
        pos += bestLen;
      } else {
        tokens.Add((0, 0, buffer[pos]));
        Insert(pos);
        ++pos;
      }
    }

    var writer = new AceBitWriter();
    for (var blockStart = 0; blockStart < tokens.Count; blockStart += MaxBlockSymbols) {
      var count = Math.Min(MaxBlockSymbols, tokens.Count - blockStart);
      var mainFreq = new long[AceConstants.Lz77NumMainCodes];
      var lenFreq = new long[AceConstants.Lz77NumLenCodes];
      for (var t = blockStart; t < blockStart + count; ++t) {
        var (length, distance, literal) = tokens[t];
        if (length == 0) {
          ++mainFreq[literal];
          continue;
        }
        ++mainFreq[260 + BitLength((uint)(distance - 1))];
        ++lenFreq[length - MinLengthFor(distance)];
      }
      var mainWidths = AceHuffman.BuildWidths(mainFreq, AceConstants.Lz77MaxCodeWidth);
      var lenWidths = AceHuffman.BuildWidths(lenFreq, AceConstants.Lz77MaxCodeWidth);
      AceHuffman.WriteTree(writer, mainWidths);
      AceHuffman.WriteTree(writer, lenWidths);
      writer.WriteBits((uint)count, 15);
      var mainCodes = AceHuffman.Codes(mainWidths, AceConstants.Lz77MaxCodeWidth);
      var lenCodes = AceHuffman.Codes(lenWidths, AceConstants.Lz77MaxCodeWidth);
      for (var t = blockStart; t < blockStart + count; ++t) {
        var (length, distance, literal) = tokens[t];
        if (length == 0) {
          var (c, w) = mainCodes[literal];
          writer.WriteBits(c, w);
          continue;
        }
        var d = (uint)(distance - 1);
        var width = BitLength(d);
        var (mc, mw) = mainCodes[260 + width];
        writer.WriteBits(mc, mw);
        writer.WriteKnownWidthUInt(d, width);
        var (lc, lw) = lenCodes[length - MinLengthFor(distance)];
        writer.WriteBits(lc, lw);
      }
    }

    if (this._solid)
      this.Remember(data);
    return writer.ToArray();
  }
}
