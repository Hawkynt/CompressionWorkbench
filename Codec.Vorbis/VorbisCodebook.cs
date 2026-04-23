#pragma warning disable CS1591

namespace Codec.Vorbis;

/// <summary>
/// Vorbis codebook. Decodes scalar symbols from the packet bitstream via a
/// Huffman tree and optionally produces VQ lookup vectors (lookup types 0/1/2).
/// Type 1 uses lattice-style implicit multiplication values; type 2 stores
/// values explicitly; type 0 means no VQ (scalar-only).
/// </summary>
internal sealed class VorbisCodebook {
  public int Entries;
  public int Dimensions;
  public int LookupType;
  public int ValueBits;
  public bool SequenceP;
  public float MinimumValue;
  public float DeltaValue;
  public int[] CodewordLengths = null!;
  public float[]? MultiplicandsScaled;
  public uint[]? Codewords;
  public int[] SortedIndex = null!;
  public uint[] SortedCodewords = null!;

  /// <summary>Parse a codebook header from the setup packet starting at the current reader position.</summary>
  public static VorbisCodebook Read(VorbisBitReader br) {
    var sync = br.ReadBits(24);
    if (sync != 0x564342)
      throw new InvalidDataException($"Vorbis: codebook sync mismatch (got 0x{sync:X6}).");

    var cb = new VorbisCodebook {
      Dimensions = (int)br.ReadBits(16),
      Entries = (int)br.ReadBits(24),
    };
    var ordered = br.ReadBits(1) != 0;
    cb.CodewordLengths = new int[cb.Entries];
    if (!ordered) {
      var sparse = br.ReadBits(1) != 0;
      for (var i = 0; i < cb.Entries; ++i) {
        if (sparse) {
          if (br.ReadBits(1) != 0)
            cb.CodewordLengths[i] = (int)br.ReadBits(5) + 1;
          else
            cb.CodewordLengths[i] = 0; // unused
        } else {
          cb.CodewordLengths[i] = (int)br.ReadBits(5) + 1;
        }
      }
    } else {
      var currentLength = (int)br.ReadBits(5) + 1;
      var i = 0;
      while (i < cb.Entries) {
        var bitsNeeded = BitsFor(cb.Entries - i);
        var number = (int)br.ReadBits(bitsNeeded);
        for (var k = 0; k < number; ++k) {
          if (i + k >= cb.Entries)
            throw new InvalidDataException("Vorbis: ordered codebook overflow.");
          cb.CodewordLengths[i + k] = currentLength;
        }
        i += number;
        currentLength++;
      }
    }

    cb.LookupType = (int)br.ReadBits(4);
    if (cb.LookupType > 2)
      throw new InvalidDataException($"Vorbis: unknown codebook lookup_type {cb.LookupType}.");
    if (cb.LookupType > 0) {
      cb.MinimumValue = br.ReadFloat32();
      cb.DeltaValue = br.ReadFloat32();
      cb.ValueBits = (int)br.ReadBits(4) + 1;
      cb.SequenceP = br.ReadBits(1) != 0;
      var lookupValues = cb.LookupType == 1
        ? LookupValues1(cb.Entries, cb.Dimensions)
        : checked(cb.Entries * cb.Dimensions);
      var multiplicands = new int[lookupValues];
      for (var i = 0; i < lookupValues; ++i)
        multiplicands[i] = (int)br.ReadBits(cb.ValueBits);

      var vqValues = new float[cb.Entries * cb.Dimensions];
      if (cb.LookupType == 1) {
        for (var entry = 0; entry < cb.Entries; ++entry) {
          double last = 0;
          var divisor = 1;
          for (var d = 0; d < cb.Dimensions; ++d) {
            var offset = (entry / divisor) % lookupValues;
            var v = multiplicands[offset] * (double)cb.DeltaValue + cb.MinimumValue + last;
            vqValues[entry * cb.Dimensions + d] = (float)v;
            if (cb.SequenceP) last = v;
            divisor *= lookupValues;
          }
        }
      } else {
        for (var entry = 0; entry < cb.Entries; ++entry) {
          double last = 0;
          var offset = entry * cb.Dimensions;
          for (var d = 0; d < cb.Dimensions; ++d) {
            var v = multiplicands[offset + d] * (double)cb.DeltaValue + cb.MinimumValue + last;
            vqValues[entry * cb.Dimensions + d] = (float)v;
            if (cb.SequenceP) last = v;
          }
        }
      }
      cb.MultiplicandsScaled = vqValues;
    }

    BuildHuffman(cb);
    return cb;
  }

  private static int LookupValues1(int entries, int dimensions) {
    // Greatest integer r such that r^dim <= entries.
    var r = (int)Math.Floor(Math.Pow(entries, 1.0 / dimensions));
    while (Pow(r + 1, dimensions) <= entries) r++;
    while (Pow(r, dimensions) > entries) r--;
    return r;
  }

  private static long Pow(int b, int e) {
    long v = 1;
    for (var i = 0; i < e; ++i) v *= b;
    return v;
  }

  private static int BitsFor(int value) {
    var bits = 0;
    while (value > 0) { bits++; value >>= 1; }
    return bits;
  }

  /// <summary>Assign Huffman codewords to lengths and build a sorted lookup.</summary>
  private static void BuildHuffman(VorbisCodebook cb) {
    var codewords = new uint[cb.Entries];
    Span<uint> available = stackalloc uint[32];
    var usedCount = 0;

    // First used entry seeds the tree.
    var firstIndex = -1;
    for (var i = 0; i < cb.Entries; ++i) if (cb.CodewordLengths[i] > 0) { firstIndex = i; break; }
    if (firstIndex < 0) {
      cb.Codewords = codewords;
      cb.SortedCodewords = [];
      cb.SortedIndex = [];
      return;
    }
    var firstLen = cb.CodewordLengths[firstIndex];
    codewords[firstIndex] = 0;
    for (var i = 1; i <= firstLen; ++i) available[i] = 1u << (32 - i);
    usedCount++;

    for (var i = firstIndex + 1; i < cb.Entries; ++i) {
      var len = cb.CodewordLengths[i];
      if (len == 0) continue;
      // Find the deepest non-zero 'available' at depth <= len.
      var depth = len;
      while (depth > 0 && available[depth] == 0) depth--;
      if (depth == 0)
        throw new InvalidDataException("Vorbis: codebook not a valid prefix code (overfull).");
      var code = available[depth];
      available[depth] = 0;
      codewords[i] = BitReverse(code) >> (32 - len);
      if (depth != len) {
        for (var d = len; d > depth; --d) {
          if (available[d] != 0)
            throw new InvalidDataException("Vorbis: codebook prefix collision.");
          available[d] = code + (1u << (32 - d));
        }
      }
      usedCount++;
    }

    _ = usedCount;
    cb.Codewords = codewords;

    // Sorted index for fast longest-prefix decode.
    var pairs = new List<(uint code, int len, int entry)>(cb.Entries);
    for (var i = 0; i < cb.Entries; ++i)
      if (cb.CodewordLengths[i] > 0)
        pairs.Add((codewords[i], cb.CodewordLengths[i], i));
    cb.SortedCodewords = new uint[pairs.Count];
    cb.SortedIndex = new int[pairs.Count];
    // Sort by codeword (ascending) for bit-shifted prefix scan.
    pairs.Sort((a, b) => a.code.CompareTo(b.code));
    for (var i = 0; i < pairs.Count; ++i) {
      cb.SortedCodewords[i] = pairs[i].code;
      cb.SortedIndex[i] = pairs[i].entry;
    }
  }

  private static uint BitReverse(uint n) {
    n = ((n & 0xAAAAAAAAu) >> 1) | ((n & 0x55555555u) << 1);
    n = ((n & 0xCCCCCCCCu) >> 2) | ((n & 0x33333333u) << 2);
    n = ((n & 0xF0F0F0F0u) >> 4) | ((n & 0x0F0F0F0Fu) << 4);
    n = ((n & 0xFF00FF00u) >> 8) | ((n & 0x00FF00FFu) << 8);
    return (n >> 16) | (n << 16);
  }

  /// <summary>
  /// Decode a scalar codebook entry by reading bits until a prefix matches.
  /// Returns the entry index (0..Entries-1).
  /// </summary>
  public int DecodeScalar(VorbisBitReader br) {
    // Read bits one at a time, growing a code until it matches something in the
    // sorted table. This is not the fastest strategy but it is correct and small.
    if (this.SortedCodewords.Length == 0) return -1;
    uint acc = 0;
    var bitsRead = 0;
    while (bitsRead < 32) {
      if (br.Eof) return -1;
      var bit = br.ReadBits(1);
      // Append as next-lower bit of codeword in LSB-first order, then shift.
      acc = (acc << 1) | bit;
      bitsRead++;
      // Scan entries with length == bitsRead: linear, but codebooks are small.
      for (var i = 0; i < this.SortedCodewords.Length; ++i) {
        var entry = this.SortedIndex[i];
        if (this.CodewordLengths[entry] == bitsRead && this.Codewords![entry] == acc)
          return entry;
      }
    }
    return -1;
  }

  /// <summary>
  /// Decode a VQ-lookup vector from a scalar entry, writing its
  /// <see cref="Dimensions"/> floats into <paramref name="output"/>.
  /// </summary>
  public bool DecodeVector(VorbisBitReader br, Span<float> output) {
    var entry = this.DecodeScalar(br);
    if (entry < 0 || this.MultiplicandsScaled == null) return false;
    var src = this.Dimensions * entry;
    for (var d = 0; d < this.Dimensions; ++d) output[d] = this.MultiplicandsScaled[src + d];
    return true;
  }
}
