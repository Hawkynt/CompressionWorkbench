namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.6 ALZ decoder (stream methods 40..87, modes <c>-m1</c>..<c>-m3</c>), implemented from
/// docs/UHARC-ON-DISK.md section "ALZ".
/// </summary>
/// <remarks>
/// Every step decodes a token from an adaptive five-symbol table: 0 switches the literal
/// model's "selector", 1 is a copy with an explicit distance, 2 repeats the last distance,
/// 3 reuses one of the eleven older distances, 4 is a literal. Selectors 1..9 hand the
/// literals to the multimedia predictors of <see cref="UharcAlzSideModel"/>.
/// </remarks>
internal sealed class UharcAlzDecoder {

  private const int RunLength = 255;
  private const int RecentCount = 12;
  private const ushort Half = 2048;

  private static readonly ushort[] TokenSeed = [0, 1, 34, 45, 57, 101];
  private static readonly ushort[] RunTokenSeed = [0, 1, 28, 78, 89, 100];

  // Literal-mode state machine: [(token - 1) * 32 + mode] -> next mode.
  private static readonly byte[] ModeNext = [
    16, 16, 16, 16, 17, 17, 17, 17, 20, 20, 20, 20, 18, 18, 18, 19, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    22, 22, 22, 22, 24, 23, 23, 24, 27, 27, 27, 27, 25, 25, 25, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28,
    29, 29, 29, 29, 29, 29, 29, 29, 30, 30, 30, 30, 30, 30, 30, 30, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31,
    0, 0, 0, 0, 1, 2, 2, 3, 4, 4, 7, 7, 5, 5, 5, 6, 8, 8, 8, 8, 8, 9, 12, 12, 12, 13, 13, 14, 15, 10, 10, 11,
  ];

  // Search trees: [node * 2 + (answer above node ? 1 : 0)] -> next node, 0 ends.
  private static readonly byte[] SlotTree = [
    0, 0, 0, 0, 1, 3, 0, 0, 2, 6, 0, 0, 5, 7, 0, 0, 4, 12, 0, 0, 9, 11, 0, 0, 10, 14, 0, 0, 13, 15, 0, 0,
    8, 32, 0, 0, 17, 19, 0, 0, 18, 22, 0, 0, 21, 23, 0, 0, 20, 28, 0, 0, 25, 27, 0, 0, 26, 30, 0, 0, 29, 31, 0, 0,
    24, 40, 0, 0, 33, 35, 0, 0, 34, 38, 0, 0, 37, 39, 0, 0, 36, 44, 0, 0, 41, 43, 0, 0, 42, 0, 0, 0,
  ];

  private static readonly byte[] RecentTree = [
    0, 0, 0, 2, 0, 4, 0, 0, 3, 8, 0, 0, 5, 7, 0, 0, 6, 10, 0, 0, 9, 0,
  ];

  private static readonly byte[] LengthTree = [
    0, 0, 0, 0, 0, 4, 0, 0, 3, 5, 0, 0, 2, 8, 0, 0, 7, 9, 0, 0, 6, 18, 0, 0, 11, 13, 0, 0, 12, 16, 0, 0,
    15, 17, 0, 0, 14, 30, 0, 0, 19, 21, 0, 0, 20, 26, 0, 0, 23, 25, 0, 0, 24, 28, 0, 0, 27, 29, 0, 0, 22, 62, 0, 0,
    31, 33, 0, 0, 32, 36, 0, 0, 35, 37, 0, 0, 34, 42, 0, 0, 39, 41, 0, 0, 40, 44, 0, 0, 43, 45, 0, 0, 38, 54, 0, 0,
    47, 49, 0, 0, 48, 52, 0, 0, 51, 53, 0, 0, 50, 58, 0, 0, 55, 57, 0, 0, 56, 60, 0, 0, 59, 61, 0, 0, 46, 254, 0, 0,
    63, 65, 0, 0, 64, 68, 0, 0, 67, 69, 0, 0, 66, 74, 0, 0, 71, 73, 0, 0, 72, 76, 0, 0, 75, 77, 0, 0, 70, 86, 0, 0,
    79, 81, 0, 0, 80, 84, 0, 0, 83, 85, 0, 0, 82, 90, 0, 0, 87, 89, 0, 0, 88, 92, 0, 0, 91, 93, 0, 0, 78, 110, 0, 0,
    95, 97, 0, 0, 96, 100, 0, 0, 99, 101, 0, 0, 98, 106, 0, 0, 103, 105, 0, 0, 104, 108, 0, 0, 107, 109, 0, 0, 102, 118, 0, 0,
    111, 113, 0, 0, 112, 116, 0, 0, 115, 117, 0, 0, 114, 122, 0, 0, 119, 121, 0, 0, 120, 124, 0, 0, 123, 125, 0, 0, 94, 190, 0, 0,
    127, 129, 0, 0, 128, 132, 0, 0, 131, 133, 0, 0, 130, 138, 0, 0, 135, 137, 0, 0, 136, 140, 0, 0, 139, 141, 0, 0, 134, 150, 0, 0,
    143, 145, 0, 0, 144, 148, 0, 0, 147, 149, 0, 0, 146, 154, 0, 0, 151, 153, 0, 0, 152, 156, 0, 0, 155, 157, 0, 0, 142, 174, 0, 0,
    159, 161, 0, 0, 160, 164, 0, 0, 163, 165, 0, 0, 162, 170, 0, 0, 167, 169, 0, 0, 168, 172, 0, 0, 171, 173, 0, 0, 166, 182, 0, 0,
    175, 177, 0, 0, 176, 180, 0, 0, 179, 181, 0, 0, 178, 186, 0, 0, 183, 185, 0, 0, 184, 188, 0, 0, 187, 189, 0, 0, 158, 222, 0, 0,
    191, 193, 0, 0, 192, 196, 0, 0, 195, 197, 0, 0, 194, 202, 0, 0, 199, 201, 0, 0, 200, 204, 0, 0, 203, 205, 0, 0, 198, 214, 0, 0,
    207, 209, 0, 0, 208, 212, 0, 0, 211, 213, 0, 0, 210, 218, 0, 0, 215, 217, 0, 0, 216, 220, 0, 0, 219, 221, 0, 0, 206, 238, 0, 0,
    223, 225, 0, 0, 224, 228, 0, 0, 227, 229, 0, 0, 226, 234, 0, 0, 231, 233, 0, 0, 232, 236, 0, 0, 235, 237, 0, 0, 230, 246, 0, 0,
    239, 241, 0, 0, 240, 244, 0, 0, 243, 245, 0, 0, 242, 250, 0, 0, 247, 249, 0, 0, 248, 252, 0, 0, 251, 253, 0, 0, 126, 0, 0, 0,
  ];

  // Distance slots: slot k covers [SlotBase[k], SlotBase[k] + SlotSpan[k]).
  private static readonly uint[] SlotBase = BuildSlotBases();
  private static readonly uint[] SlotSpan = [
    1, 2, 4, 8, 16, 32, 32, 32, 64, 64, 128, 128, 256, 256, 512, 512,
    1024, 1024, 2048, 2048, 4096, 4096, 8192, 8192, 16384, 16384, 32768, 32768, 65536, 65536, 131072, 131072,
    262144, 262144, 524288, 524288, 1048576, 1048576, 2097152, 2097152, 4194304, 4194304, 8388608, 8388608,
  ];

  private readonly UharcRangeDecoder _coder;
  private readonly UharcAlzLiteralModel _literals;
  private readonly UharcAlzSideModel _side = new();
  private readonly byte[] _window;
  private readonly uint _mask;
  private readonly uint _dictionarySize;

  private readonly ushort[,] _tokens = new ushort[66, 6];
  private readonly byte[] _tokenHistory = new byte[65536];
  private readonly ushort[] _slotProbability = new ushort[64];
  private readonly ushort[] _recentProbability = new ushort[64];
  private ushort _lowBitProbability = Half;
  private readonly ushort[] _lengthProbability = new ushort[8192];
  private readonly byte[] _lengthHistory = new byte[0x40000];
  private readonly uint[] _recent = new uint[RecentCount];

  private uint _writePosition;
  private uint _history;
  private int _literalMode;
  private uint _runCount;
  private int _selector;
  private bool _alternativeText;

  private UharcAlzDecoder(byte[] packed, int offset, int length, int method) {
    this._dictionarySize = (uint)UharcMethods.DictionarySize(method);
    this._window = new byte[this._dictionarySize];
    this._mask = this._dictionarySize - 1;

    for (var row = 0; row < 66; ++row) {
      var seed = row < 64 ? TokenSeed : RunTokenSeed;
      for (var i = 0; i < 6; ++i)
        this._tokens[row, i] = seed[i];
    }

    Array.Fill(this._slotProbability, Half);
    Array.Fill(this._recentProbability, Half);
    Array.Fill(this._lengthProbability, Half);
    Array.Fill(this._recent, 1u);

    this._coder = new UharcRangeDecoder(packed, offset, length);
    this._literals = new UharcAlzLiteralModel(this._coder);
    this.DecodeSelector();
  }

  /// <summary>Decodes <paramref name="unpackedSize"/> bytes of an ALZ stream.</summary>
  public static byte[] Decode(byte[] packed, int offset, int length, int method, int unpackedSize, IReadOnlyList<long> fileEnds) {
    if (method is < UharcMethods.AlzFirst or > UharcMethods.AlzLast)
      throw new ArgumentOutOfRangeException(nameof(method));

    var output = new byte[unpackedSize];
    if (unpackedSize == 0)
      return output;

    var decoder = new UharcAlzDecoder(packed, offset, length, method);
    var resets = new UharcModelResets(fileEnds, unpackedSize, UharcModelResets.AlzSpan, mergeSmallFiles: false);
    var position = 0;
    while (position < unpackedSize) {
      if (resets.IsResetPoint(position))
        decoder.ResetForNextFile();

      position = decoder.DecodeStep(output, position);
    }

    return output;
  }

  private void ResetForNextFile() {
    this._literals.ResetForNextFile();
    for (var row = 0; row < 66; ++row) {
      var previous = (uint)this._tokens[row, 1];
      var sum = previous;
      for (var i = 2; i < 6; ++i) {
        var current = (uint)this._tokens[row, i];
        sum += (current - previous + 1) >> 1;
        this._tokens[row, i] = (ushort)sum;
        previous = current;
      }
    }

    this.DecodeSelector();
  }

  private void DecodeSelector() {
    var selector = (int)this._coder.DecodeUniform(11);
    if (selector == this._selector) {
      this._alternativeText = false;
      return;
    }

    this._alternativeText = selector == 10;
    if (selector == 10) {
      this._literals.ForgetAlternativeContexts();
      selector = 0;
    }

    this._selector = selector;
    this._side.Select(selector);
  }

  private int DecodeStep(byte[] output, int position) {
    int token;
    var escapes = 0;
    while ((token = this.DecodeToken()) == 0) {
      this.DecodeSelector();
      if (++escapes > 64)
        throw new InvalidDataException("UHARC: ALZ selector loop.");
    }

    if (token == 4) {
      var context = (int)(this._history & 0xFF);
      var before = (int)((this._history >> 8) & 0xFF);
      int primary, escape;
      if (this._selector != 0) {
        primary = this._side.Context;
        escape = this._side.EscapeContext;
        if (this._literals.Excluded >= 0)
          this._literals.Excluded = this._side.ToDifference(this._literals.Excluded);
      } else if (this._alternativeText) {
        primary = context + 0x200;
        escape = 0x401;
      } else {
        primary = (context & 0xC0) == (before & 0xC0) ? context + 0x100 : context;
        escape = ((context & 0xE0) >> 5) + 0x3F9;
      }

      this._literals.Side = this._selector != 0 ? this._side : null;
      var symbol = this._literals.Decode(primary, escape);
      var value = this._selector != 0 ? this._side.ToByte(symbol) : (byte)symbol;
      this._literals.Excluded = -1;
      this._runCount = 0;
      return this.WriteByte(output, position, value);
    }

    var (distance, count) = this.DecodeCopy(token, position);
    if (count > output.Length - position)
      throw new InvalidDataException("UHARC: ALZ copy runs past the end of the stream.");

    var source = (this._writePosition - distance) & this._mask;
    for (var i = 0; i < count; ++i) {
      position = this.WriteByte(output, position, this._window[source]);
      source = (source + 1) & this._mask;
    }

    if (count == RunLength) {
      this._literals.Excluded = -1;
      ++this._runCount;
    } else {
      this._literals.Excluded = this._window[source];
      this._runCount = 0;
    }

    return position;
  }

  private int WriteByte(byte[] output, int position, byte value) {
    output[position] = value;
    this._window[this._writePosition] = value;
    this._writePosition = (this._writePosition + 1) & this._mask;
    this._history = (this._history << 8) | value;
    if (this._selector != 0)
      this._side.Observe(value, this._history, position + 1);
    return position + 1;
  }

  // ── Tokens ─────────────────────────────────────────────────────────────

  private int DecodeToken() {
    var key = (int)(this._history & 0xFFFF);
    var fromHistory = false;
    int row;
    if (this._runCount != 0)
      row = this._runCount > 2 ? 65 : 64;
    else {
      row = this._literalMode;
      if (row <= 15) {
        fromHistory = true;
        var remembered = this._tokenHistory[key];
        if ((remembered & 0x38) != 0)
          row = 32 + (remembered >> 3);
      }
    }

    var count = this._coder.GetCount(this._tokens[row, 5]);
    var token = 4;
    while (token > 0 && count < this._tokens[row, token])
      --token;

    this._coder.Consume(this._tokens[row, token], (uint)(this._tokens[row, token + 1] - this._tokens[row, token]));
    if (token == 0)
      return 0;

    for (var i = token + 1; i < 6; ++i)
      this._tokens[row, i] += 88;

    // Halve the four token frequencies; the escape slot keeps its width of 1.
    if (this._tokens[row, 5] >= 16384) {
      Span<uint> width = stackalloc uint[5];
      for (var i = 1; i < 5; ++i)
        width[i] = (uint)(this._tokens[row, i + 1] - this._tokens[row, i] + 1) >> 1;
      for (var i = 1; i < 5; ++i)
        this._tokens[row, i + 1] = (ushort)(this._tokens[row, i] + width[i]);
    }

    if (fromHistory) {
      var tag = (token - 1) & 3;
      var remembered = this._tokenHistory[key];
      if (remembered >> 6 == tag) {
        if ((remembered & 63) != 63)
          ++remembered;
      } else
        remembered = (byte)(tag << 6);
      this._tokenHistory[key] = remembered;
    }

    this._literalMode = ModeNext[((token - 1) << 5) + (this._literalMode & 31)];
    return token;
  }

  // ── Copies ─────────────────────────────────────────────────────────────

  private (uint Distance, int Count) DecodeCopy(int token, int position) {
    uint distance;
    int slot;
    switch (token) {
      case 1:
        distance = this.DecodeDistance(position);
        slot = RecentCount - 1;
        break;
      case 2:
        distance = this._recent[0];
        slot = 0;
        break;
      default:
        slot = SearchTree(this._recentProbability, RecentTree, 1, 11);
        distance = this._recent[slot];
        break;
    }

    var count = this.DecodeLength(token, distance);
    if ((distance > 1 || this._runCount > 1) && slot != 0) {
      for (var i = slot; i > 0; --i)
        this._recent[i] = this._recent[i - 1];
      this._recent[0] = distance;
    }

    if (distance == 0 || distance > (uint)position || distance > this._dictionarySize)
      throw new InvalidDataException("UHARC: ALZ copy reaches before the start of the stream.");

    return (distance, count);
  }

  private int SearchTree(ushort[] probabilities, byte[] tree, int root, int fallback) {
    var node = root;
    var result = fallback;
    while (node != 0) {
      if (this._coder.DecodeBit(ref probabilities[node], 6) == 0)
        result = node;
      node = tree[node * 2 + (result > node ? 1 : 0)];
    }

    return result;
  }

  private uint DecodeDistance(int position) {
    var k = SearchTree(this._slotProbability, SlotTree, 16, 44) - 1;
    var low = SlotBase[k];
    if (k == 0)
      return low;

    var span = SlotSpan[k];
    if (low > (uint)position)
      throw new InvalidDataException("UHARC: ALZ distance slot beyond the decoded data.");
    if (low + span - 1 > (uint)position)
      span = (uint)position - low + 1;

    var lowBit = -1;
    if (span >= 4) {
      lowBit = this._coder.DecodeBit(ref this._lowBitProbability, 6);
      span >>= 1;
    }

    var extra = 0u;
    if (span > 0x800) {
      var blocks = ((span - 1) >> 11) + 1;
      var block = this._coder.DecodeUniform(blocks);
      extra = block << 11;
      span = block + 1 != blocks ? 0x800 : span - extra;
    }

    if (span >= 2)
      extra += this._coder.DecodeUniform(span);
    if (lowBit >= 0)
      extra = extra * 2 + (uint)lowBit;

    return low + extra;
  }

  private int DecodeLength(int token, uint distance) {
    int group, forced = 2;
    if (token == 1) {
      group = distance switch {
        < 0x100 => 0,
        < 0x1000 => 1,
        < 0x10000 => 2,
        < 0x100000 => 3,
        _ => 4,
      };
      forced = group + 3;
    } else
      group = this._runCount == 0 ? 5 : this._runCount > 2 ? 7 : 6;

    var key = -1;
    var previous = 0;
    var older = 0;
    if (group <= 5 && distance > 1) {
      key = this._window[(this._writePosition - distance) & this._mask] << 8
        | this._window[(this._writePosition - distance + 1) & this._mask];
      if (group == 5)
        key += 0x10000;
      previous = this._lengthHistory[key];
      older = this._lengthHistory[key + 0x20000];
      this._lengthHistory[key + 0x20000] = (byte)previous;
    }

    var node = 10;
    int result;
    while (true) {
      result = node + 1;
      if (node >= forced) {
        var index = group * 1024 + (node >= previous ? 256 : 0) + (older <= node ? 512 : 0) + node;
        if (this._coder.DecodeBit(ref this._lengthProbability[index], 5) == 0)
          result = node;
      }

      node = LengthTree[node * 2 + (node < result ? 1 : 0)];
      if (node == 0)
        break;
    }

    result &= 0xFF;
    if (key >= 0)
      this._lengthHistory[key] = (byte)result;
    return result;
  }

  private static uint[] BuildSlotBases() {
    var bases = new uint[44];
    for (var i = 0; i < bases.Length; ++i)
      bases[i] = i < 6
        ? 1u << i
        : ((i - 6) & 1) == 0 ? 1u << (6 + (i - 6) / 2) : 3u << (5 + (i - 6) / 2);
    return bases;
  }
}
