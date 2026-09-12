namespace FileFormat.Freeze;

/// <summary>
/// Reads and writes the interoperable Freeze 1.x and 2.x stream formats used by the original
/// <c>freeze</c>/<c>melt</c> utilities.
/// </summary>
/// <remarks>
/// Freeze 1.x and 2.x are wire-incompatible. The former uses <c>1F 9E</c>, a 4 KiB ring,
/// 60-byte maximum matches and a fixed position-Huffman table. Freeze 2.x uses <c>1F 9F</c>,
/// an 8 KiB ring, 256-byte maximum matches and a three-byte per-stream position-table description.
/// This implementation was derived independently from the published format behavior; GPL reference
/// implementations are used only as behavioral interoperability oracles.
/// </remarks>
public static class FreezeStream {

  private const byte Magic0 = 0x1F;
  private const byte Freeze1Magic = 0x9E;
  private const byte Freeze2Magic = 0x9F;
  private const int MinMatchLength = 3;
  private const int HashBits = 15;
  private const int HashSize = 1 << HashBits;
  private const int EndSymbol = 256;
  private const int MaxFrequency = 0x8000;

  // Number of position-prefix codes of lengths 1..8. Index zero is unused.
  private static readonly byte[] Freeze1PositionTable = [0, 0, 0, 1, 3, 8, 12, 24, 16];
  private static readonly byte[] Freeze2PositionTable = [0, 0, 1, 1, 1, 4, 10, 27, 18];

  private sealed record FreezeProfile(
      FreezeCompatibility Compatibility,
      byte VersionMagic,
      int RingSize,
      int MaxMatchLength,
      int MaxDistance,
      int PositionSymbolCount,
      int PositionLowBits,
      bool HasPositionHeader,
      byte[] DefaultPositionTable) {
    public int SymbolCount => 255 + this.MaxMatchLength;
    public int InitialWritePosition => this.RingSize - this.MaxMatchLength;
    public int RingMask => this.RingSize - 1;
  }

  private static readonly FreezeProfile Freeze1Profile = new(
    FreezeCompatibility.Freeze1x,
    Freeze1Magic,
    RingSize: 4096,
    MaxMatchLength: 60,
    MaxDistance: 4096,
    PositionSymbolCount: 64,
    PositionLowBits: 6,
    HasPositionHeader: false,
    DefaultPositionTable: Freeze1PositionTable);

  private static readonly FreezeProfile Freeze2Profile = new(
    FreezeCompatibility.Freeze2x,
    Freeze2Magic,
    RingSize: 8192,
    MaxMatchLength: 256,
    MaxDistance: 7936,
    PositionSymbolCount: 62,
    PositionLowBits: 7,
    HasPositionHeader: true,
    DefaultPositionTable: Freeze2PositionTable);

  /// <summary>
  /// Compresses data from <paramref name="input"/> using the default Freeze 2.x compatibility target.
  /// </summary>
  public static void Compress(Stream input, Stream output) =>
    Compress(input, output, new FreezeCompressionOptions());

  /// <summary>
  /// Compresses data using the requested compatibility target, LZ match search and position-table strategy.
  /// </summary>
  public static void Compress(Stream input, Stream output, FreezeCompressionOptions options) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(options);
    if (options.SearchDepth <= 0)
      throw new ArgumentOutOfRangeException(nameof(options), options.SearchDepth, "Freeze search depth must be positive.");

    var profile = GetProfile(options.TargetCompatibility);
    if (!profile.HasPositionHeader && options.PositionTable == FreezePositionTableMode.Optimized)
      throw new NotSupportedException("Freeze 1.x has a fixed position Huffman table and cannot carry a per-stream optimized table.");

    using var sourceBuffer = new MemoryStream();
    input.CopyTo(sourceBuffer);
    var source = sourceBuffer.ToArray();
    var tokens = Lz77Parse(source, options, profile);

    var positionTable = options.PositionTable == FreezePositionTableMode.Optimized
      ? BuildOptimizedPositionTable(tokens, profile)
      : [.. profile.DefaultPositionTable];
    var positionCodes = PositionCodeTable.Create(positionTable, profile.PositionSymbolCount, profile.PositionLowBits);

    output.WriteByte(Magic0);
    output.WriteByte(profile.VersionMagic);
    if (profile.HasPositionHeader)
      WritePositionHeader(output, positionTable);

    var writer = new BitWriter(output);
    var huffman = new AdaptiveHuffman(profile.SymbolCount);
    foreach (var token in tokens) {
      if (token.Length == 0) {
        huffman.Encode(writer, token.Literal);
        continue;
      }

      huffman.Encode(writer, 256 + token.Length - 2);
      positionCodes.Encode(writer, token.Distance - 1);
    }

    huffman.Encode(writer, EndSymbol);
    writer.Flush();
  }

  /// <summary>
  /// Decompresses a Freeze 1.x or 2.x stream. The magic selects the historical decoder profile.
  /// </summary>
  /// <exception cref="InvalidDataException">The stream is truncated or structurally invalid.</exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var magic0 = ReadRequiredByte(input);
    var versionMagic = ReadRequiredByte(input);
    if (magic0 != Magic0)
      throw new InvalidDataException($"Invalid Freeze magic: 0x{magic0:X2}{versionMagic:X2}.");

    var profile = versionMagic switch {
      Freeze1Magic => Freeze1Profile,
      Freeze2Magic => Freeze2Profile,
      _ => throw new InvalidDataException($"Unsupported Freeze version magic: 0x{magic0:X2}{versionMagic:X2}."),
    };

    var positionTable = profile.HasPositionHeader
      ? ReadPositionHeader(input, profile)
      : [.. profile.DefaultPositionTable];
    var positionCodes = PositionCodeTable.Create(positionTable, profile.PositionSymbolCount, profile.PositionLowBits);
    var reader = new BitReader(input);
    var huffman = new AdaptiveHuffman(profile.SymbolCount);

    var ring = new byte[profile.RingSize];
    Array.Fill(ring, (byte)' ', 0, profile.InitialWritePosition);
    var writePosition = profile.InitialWritePosition;

    while (true) {
      var symbol = huffman.Decode(reader);
      if (symbol == EndSymbol)
        return;

      if (symbol < 256) {
        var value = (byte)symbol;
        output.WriteByte(value);
        ring[writePosition] = value;
        writePosition = (writePosition + 1) & profile.RingMask;
        continue;
      }

      var length = symbol - 256 + 2;
      if (length is < MinMatchLength || length > profile.MaxMatchLength)
        throw new InvalidDataException($"Invalid Freeze match length {length} for {profile.Compatibility}.");

      var encodedPosition = positionCodes.Decode(reader);
      if ((uint)encodedPosition >= (uint)profile.MaxDistance)
        throw new InvalidDataException($"Invalid Freeze match position {encodedPosition} for {profile.Compatibility}.");

      var distance = encodedPosition + 1;
      var readPosition = (writePosition - distance) & profile.RingMask;
      for (var i = 0; i < length; i++) {
        var value = ring[(readPosition + i) & profile.RingMask];
        output.WriteByte(value);
        ring[writePosition] = value;
        writePosition = (writePosition + 1) & profile.RingMask;
      }
    }
  }

  private static FreezeProfile GetProfile(FreezeCompatibility compatibility) => compatibility switch {
    FreezeCompatibility.Freeze1x => Freeze1Profile,
    FreezeCompatibility.Freeze2x => Freeze2Profile,
    _ => throw new ArgumentOutOfRangeException(nameof(compatibility), compatibility, "Unknown Freeze compatibility target."),
  };

  #region LZ parsing

  private readonly record struct Token(byte Literal, int Length, int Distance);
  private readonly record struct Match(int Length, int Distance);

  private static List<Token> Lz77Parse(byte[] source, FreezeCompressionOptions options, FreezeProfile profile) {
    var tokens = new List<Token>();
    var hashTable = new int[HashSize];
    var chain = new int[source.Length];
    Array.Fill(hashTable, -1);
    Array.Fill(chain, -1);

    var position = 0;
    while (position < source.Length) {
      var match = FindBestMatch(source, position, hashTable, chain, options.SearchDepth, profile);
      if (match.Length >= MinMatchLength) {
        if (options.Parsing == FreezeParsingStrategy.Lazy && position + MinMatchLength < source.Length) {
          InsertHash(hashTable, chain, source, position);
          var nextMatch = FindBestMatch(source, position + 1, hashTable, chain, options.SearchDepth, profile);
          if (nextMatch.Length >= match.Length) {
            tokens.Add(new Token(source[position], 0, 0));
            position++;
            continue;
          }

          tokens.Add(new Token(0, match.Length, match.Distance));
          for (var i = 1; i < match.Length; i++)
            InsertHash(hashTable, chain, source, position + i);
          position += match.Length;
          continue;
        }

        tokens.Add(new Token(0, match.Length, match.Distance));
        for (var i = 0; i < match.Length; i++)
          InsertHash(hashTable, chain, source, position + i);
        position += match.Length;
        continue;
      }

      tokens.Add(new Token(source[position], 0, 0));
      InsertHash(hashTable, chain, source, position);
      position++;
    }

    return tokens;
  }

  private static Match FindBestMatch(
      byte[] source,
      int position,
      int[] hashTable,
      int[] chain,
      int searchDepth,
      FreezeProfile profile) {
    if (position + MinMatchLength > source.Length)
      return default;

    var bestLength = 0;
    var bestDistance = 0;
    var candidate = hashTable[Hash3(source, position)];
    var minimumPosition = Math.Max(0, position - profile.MaxDistance);
    var maximumLength = Math.Min(source.Length - position, profile.MaxMatchLength);
    var attempts = searchDepth;

    while (candidate >= minimumPosition && attempts-- > 0) {
      var length = 0;
      while (length < maximumLength && source[candidate + length] == source[position + length])
        length++;

      if (length >= MinMatchLength && length > bestLength) {
        bestLength = length;
        bestDistance = position - candidate;
        if (length == maximumLength)
          break;
      }

      candidate = chain[candidate];
    }

    return new Match(bestLength, bestDistance);
  }

  private static int Hash3(byte[] data, int position) =>
    (((data[position] << 8) | data[position + 1]) * 0x9E37 + data[position + 2]) & (HashSize - 1);

  private static void InsertHash(int[] hashTable, int[] chain, byte[] data, int position) {
    if (position + 2 >= data.Length)
      return;

    var hash = Hash3(data, position);
    chain[position] = hashTable[hash];
    hashTable[hash] = position;
  }

  #endregion

  #region Position Huffman table

  private static byte[] BuildOptimizedPositionTable(IReadOnlyList<Token> tokens, FreezeProfile profile) {
    var frequencies = new int[profile.PositionSymbolCount];
    var references = 0;
    foreach (var token in tokens) {
      if (token.Length == 0)
        continue;
      frequencies[(token.Distance - 1) >> profile.PositionLowBits]++;
      references++;
    }

    if (references == 0)
      return [.. profile.DefaultPositionTable];

    const long Infinity = long.MaxValue / 4;
    var memo = new Dictionary<(int Index, int MinimumLength, int UsedKraft), (long Cost, byte Length)>();

    long Solve(int index, int minimumLength, int usedKraft) {
      if (index == profile.PositionSymbolCount)
        return usedKraft == 256 ? 0 : Infinity;

      var key = (index, minimumLength, usedKraft);
      if (memo.TryGetValue(key, out var cached))
        return cached.Cost;

      var remainingSymbols = profile.PositionSymbolCount - index - 1;
      var bestCost = Infinity;
      byte bestLength = 0;

      for (var length = minimumLength; length <= 8; length++) {
        var kraft = 1 << (8 - length);
        var nextKraft = usedKraft + kraft;
        if (nextKraft > 256)
          continue;

        if (nextKraft + remainingSymbols > 256
            || nextKraft + remainingSymbols * kraft < 256)
          continue;

        var tailCost = Solve(index + 1, length, nextKraft);
        if (tailCost >= Infinity)
          continue;

        var cost = tailCost + (long)frequencies[index] * length;
        if (cost >= bestCost)
          continue;

        bestCost = cost;
        bestLength = (byte)length;
      }

      memo[key] = (bestCost, bestLength);
      return bestCost;
    }

    if (Solve(0, 1, 0) >= Infinity)
      return [.. profile.DefaultPositionTable];

    var table = new byte[9];
    var currentIndex = 0;
    var currentMinimum = 1;
    var currentKraft = 0;
    while (currentIndex < profile.PositionSymbolCount) {
      var choice = memo[(currentIndex, currentMinimum, currentKraft)].Length;
      if (choice == 0)
        return [.. profile.DefaultPositionTable];
      table[choice]++;
      currentKraft += 1 << (8 - choice);
      currentMinimum = choice;
      currentIndex++;
    }

    _ = PositionCodeTable.Create(table, profile.PositionSymbolCount, profile.PositionLowBits);
    return table;
  }

  private static void WritePositionHeader(Stream output, ReadOnlySpan<byte> table) {
    var packed = table[5] & 0x1F;
    packed = (packed << 4) | (table[4] & 0x0F);
    packed = (packed << 3) | (table[3] & 0x07);
    packed = (packed << 2) | (table[2] & 0x03);
    packed = (packed << 1) | (table[1] & 0x01);

    output.WriteByte((byte)packed);
    output.WriteByte((byte)(packed >> 8));
    output.WriteByte((byte)(table[6] & 0x3F));
  }

  private static byte[] ReadPositionHeader(Stream input, FreezeProfile profile) {
    var packed = ReadRequiredByte(input) | (ReadRequiredByte(input) << 8);
    var third = ReadRequiredByte(input);

    var table = new byte[9];
    table[1] = (byte)(packed & 1); packed >>= 1;
    table[2] = (byte)(packed & 3); packed >>= 2;
    table[3] = (byte)(packed & 7); packed >>= 3;
    table[4] = (byte)(packed & 0x0F); packed >>= 4;
    table[5] = (byte)(packed & 0x1F); packed >>= 5;

    if ((packed & 1) != 0 || (third & 0xC0) != 0)
      throw new InvalidDataException("Unsupported Freeze 2.x header flags.");

    table[6] = (byte)(third & 0x3F);
    var remainingSymbols = profile.PositionSymbolCount
                           - table[1] - table[2] - table[3] - table[4] - table[5] - table[6];
    var remainingKraft = 256
                         - 128 * table[1] - 64 * table[2] - 32 * table[3]
                         - 16 * table[4] - 8 * table[5] - 4 * table[6];
    var sevenBitCodes = remainingKraft - remainingSymbols;
    if (remainingSymbols < 0 || sevenBitCodes < 0 || sevenBitCodes > remainingSymbols)
      throw new InvalidDataException("Invalid Freeze position Huffman table.");

    table[7] = (byte)sevenBitCodes;
    table[8] = (byte)(remainingSymbols - sevenBitCodes);
    return table;
  }

  private sealed class PositionCodeTable {
    private readonly int[] _firstCode = new int[9];
    private readonly int[] _firstSymbol = new int[9];
    private readonly byte[] _counts = new byte[9];
    private readonly ushort[] _codes;
    private readonly byte[] _lengths;
    private readonly int _positionSymbolCount;
    private readonly int _lowBits;

    private PositionCodeTable(ReadOnlySpan<byte> table, int positionSymbolCount, int lowBits) {
      this._positionSymbolCount = positionSymbolCount;
      this._lowBits = lowBits;
      this._codes = new ushort[positionSymbolCount];
      this._lengths = new byte[positionSymbolCount];

      var symbol = 0;
      var code = 0;
      var kraft = 0;
      for (var length = 1; length <= 8; length++) {
        code <<= 1;
        this._firstCode[length] = code;
        this._firstSymbol[length] = symbol;
        this._counts[length] = table[length];

        for (var i = 0; i < table[length]; i++) {
          if (symbol >= positionSymbolCount)
            throw new InvalidDataException("Freeze position table declares too many symbols.");
          this._codes[symbol] = (ushort)code++;
          this._lengths[symbol] = (byte)length;
          symbol++;
        }

        kraft += table[length] << (8 - length);
      }

      if (symbol != positionSymbolCount || kraft != 256)
        throw new InvalidDataException("Invalid Freeze position Huffman table.");
    }

    public static PositionCodeTable Create(ReadOnlySpan<byte> table, int positionSymbolCount, int lowBits) {
      if (table.Length < 9)
        throw new InvalidDataException("Freeze position Huffman table is incomplete.");
      return new PositionCodeTable(table, positionSymbolCount, lowBits);
    }

    public void Encode(BitWriter writer, int position) {
      var maximumPosition = this._positionSymbolCount << this._lowBits;
      if ((uint)position >= (uint)maximumPosition)
        throw new InvalidDataException($"Freeze match position {position} is outside the encodable window.");

      var upper = position >> this._lowBits;
      writer.WriteBits(this._codes[upper], this._lengths[upper]);
      writer.WriteBits(position & ((1 << this._lowBits) - 1), this._lowBits);
    }

    public int Decode(BitReader reader) {
      var code = 0;
      for (var length = 1; length <= 8; length++) {
        code = (code << 1) | reader.ReadBit();
        var offset = code - this._firstCode[length];
        if ((uint)offset < this._counts[length]) {
          var upper = this._firstSymbol[length] + offset;
          return (upper << this._lowBits) | reader.ReadBits(this._lowBits);
        }
      }

      throw new InvalidDataException("Invalid Freeze position Huffman code.");
    }
  }

  #endregion

  #region Adaptive Huffman coding

  private sealed class AdaptiveHuffman {
    private readonly int _symbolCount;
    private readonly int _treeSize;
    private readonly int _root;
    private readonly int[] _frequency;
    private readonly int[] _son;
    private readonly int[] _parent;

    public AdaptiveHuffman(int symbolCount) {
      this._symbolCount = symbolCount;
      this._treeSize = symbolCount * 2 - 1;
      this._root = this._treeSize - 1;
      this._frequency = new int[this._treeSize + 1];
      this._son = new int[this._treeSize];
      this._parent = new int[this._treeSize + symbolCount];

      for (var i = 0; i < symbolCount; i++) {
        this._frequency[i] = 1;
        this._son[i] = i + this._treeSize;
        this._parent[i + this._treeSize] = i;
      }

      var child = 0;
      for (var node = symbolCount; node <= this._root; node++, child += 2) {
        this._frequency[node] = this._frequency[child] + this._frequency[child + 1];
        this._son[node] = child;
        this._parent[child] = node;
        this._parent[child + 1] = node;
      }

      this._frequency[this._treeSize] = 0xFFFF;
      this._parent[this._root] = 0;
    }

    public void Encode(BitWriter writer, int symbol) {
      if ((uint)symbol >= (uint)this._symbolCount)
        throw new ArgumentOutOfRangeException(nameof(symbol));

      Span<byte> path = stackalloc byte[this._symbolCount];
      var depth = 0;
      var node = this._parent[symbol + this._treeSize];
      while (true) {
        path[depth++] = (byte)(node & 1);
        node = this._parent[node];
        if (node == this._root)
          break;
      }

      for (var i = depth - 1; i >= 0; i--)
        writer.WriteBit(path[i]);
      this.Update(symbol);
    }

    public int Decode(BitReader reader) {
      var node = this._son[this._root];
      while (node < this._treeSize) {
        node += reader.ReadBit();
        node = this._son[node];
      }

      var symbol = node - this._treeSize;
      if ((uint)symbol >= (uint)this._symbolCount)
        throw new InvalidDataException("Invalid Freeze adaptive Huffman symbol.");
      this.Update(symbol);
      return symbol;
    }

    private void Update(int symbol) {
      if (this._frequency[this._root] == MaxFrequency)
        this.Reconstruct();

      var node = this._parent[symbol + this._treeSize];
      do {
        var frequency = ++this._frequency[node];
        var sibling = node + 1;
        if (frequency > this._frequency[sibling]) {
          var probe = sibling + 1;
          while (frequency > this._frequency[probe])
            probe++;
          var swap = probe - 1;

          this._frequency[node] = this._frequency[swap];
          this._frequency[swap] = frequency;

          var nodeSon = this._son[node];
          this._parent[nodeSon] = swap;
          if (nodeSon < this._treeSize)
            this._parent[nodeSon + 1] = swap;

          var swapSon = this._son[swap];
          this._son[swap] = nodeSon;
          this._parent[swapSon] = node;
          if (swapSon < this._treeSize)
            this._parent[swapSon + 1] = node;
          this._son[node] = swapSon;
          node = swap;
        }
      } while ((node = this._parent[node]) != 0);
    }

    private void Reconstruct() {
      var leafCount = 0;
      for (var i = 0; i < this._treeSize; i++) {
        if (this._son[i] < this._treeSize)
          continue;
        this._frequency[leafCount] = (this._frequency[i] + 1) / 2;
        this._son[leafCount] = this._son[i];
        leafCount++;
      }

      var left = 0;
      for (var node = this._symbolCount; node < this._treeSize; node++, left += 2) {
        var frequency = this._frequency[left] + this._frequency[left + 1];
        var insertAt = node - 1;
        while (insertAt >= 0 && frequency < this._frequency[insertAt])
          insertAt--;
        insertAt++;

        for (var i = node; i > insertAt; i--) {
          this._frequency[i] = this._frequency[i - 1];
          this._son[i] = this._son[i - 1];
        }
        this._frequency[insertAt] = frequency;
        this._son[insertAt] = left;
      }

      for (var i = 0; i < this._treeSize; i++) {
        var child = this._son[i];
        if (child >= this._treeSize) {
          this._parent[child] = i;
        } else {
          this._parent[child] = i;
          this._parent[child + 1] = i;
        }
      }
    }
  }

  #endregion

  #region Bit I/O

  private sealed class BitWriter(Stream output) {
    private int _currentByte;
    private int _bitsUsed;

    public void WriteBit(int bit) {
      this._currentByte = (this._currentByte << 1) | (bit & 1);
      if (++this._bitsUsed != 8)
        return;
      output.WriteByte((byte)this._currentByte);
      this._currentByte = 0;
      this._bitsUsed = 0;
    }

    public void WriteBits(int value, int count) {
      for (var i = count - 1; i >= 0; i--)
        this.WriteBit(value >> i);
    }

    public void Flush() {
      if (this._bitsUsed == 0)
        return;
      output.WriteByte((byte)(this._currentByte << (8 - this._bitsUsed)));
      this._currentByte = 0;
      this._bitsUsed = 0;
    }
  }

  private sealed class BitReader(Stream input) {
    private int _currentByte;
    private int _bitsRemaining;

    public int ReadBit() {
      if (this._bitsRemaining == 0) {
        this._currentByte = input.ReadByte();
        if (this._currentByte < 0)
          throw new InvalidDataException("Unexpected end of Freeze compressed data.");
        this._bitsRemaining = 8;
      }

      return (this._currentByte >> --this._bitsRemaining) & 1;
    }

    public int ReadBits(int count) {
      var value = 0;
      for (var i = 0; i < count; i++)
        value = (value << 1) | this.ReadBit();
      return value;
    }
  }

  private static int ReadRequiredByte(Stream input) {
    var value = input.ReadByte();
    if (value < 0)
      throw new InvalidDataException("Unexpected end of Freeze header.");
    return value;
  }

  #endregion
}
