namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.3 PPM decoder (stream methods 22..35, the <c>-mx</c> mode), implemented from
/// docs/UHARC-ON-DISK.md section "UHARC 0.3 PPM". The model is <see cref="Uharc03PpmModel"/>.
/// </summary>
internal static class Uharc03PpmDecoder {

  /// <summary>Decodes <paramref name="unpackedSize"/> bytes of a 0.3 PPM stream.</summary>
  /// <param name="packed">Buffer holding the packed stream.</param>
  /// <param name="offset">Start of the packed stream.</param>
  /// <param name="length">Length of the packed stream.</param>
  /// <param name="method">Stream method byte, 22..35.</param>
  /// <param name="unpackedSize">Number of bytes to produce.</param>
  /// <param name="fileEnds">Cumulative end offsets of the archived files.</param>
  /// <param name="recordModel">The header's record-file flag (filters bit 6).</param>
  public static byte[] Decode(byte[] packed, int offset, int length, int method, int unpackedSize, IReadOnlyList<long> fileEnds, bool recordModel = false) {
    if (method is < UharcMethods.Ppm03First or >= UharcMethods.Alz03First)
      throw new ArgumentOutOfRangeException(nameof(method));

    var output = new byte[unpackedSize];
    if (unpackedSize == 0)
      return output;

    var model = new Uharc03PpmModel(new Uharc03RangeDecoder(packed, offset, length), method);
    if (recordModel)
      model.UseRecordModel(fileEnds);
    model.Run(output, fileEnds);
    return output;
  }
}

/// <summary>
/// The UHARC 0.3 PPM model (docs/UHARC-ON-DISK.md, "UHARC 0.3 PPM"): the skeleton 0.4 refined.
/// Every step first tries a copy predicted from hashed contexts; otherwise one literal goes
/// through an order-4 record, an order-2 record, a binary-data predictor of eight sparse
/// contexts, and finally frequency rows. Against 0.4 the copy decision keys on a three-bit
/// outcome history instead of runs, the records code without learned escape shares, and the
/// stride detector keeps its candidate distances in a ranked list.
/// </summary>
internal sealed partial class Uharc03PpmModel {

  private const uint NoPosition = uint.MaxValue;
  private const int HashSize = 0x40000;
  private const int RecordFileSize = 0xFB670;
  private const int CopyContexts = 48;

  /// <summary>Copy decision seeds: the slot where "miss" starts, out of 0x1000, per context.</summary>
  private static readonly ushort[] CopySeed = [
    1024, 512, 640, 512, 768, 512, 640, 512, 512, 256, 320, 256, 384, 256, 320, 256,
    2048, 1024, 1280, 1024, 1536, 1024, 1280, 1024, 512, 256, 320, 256, 384, 256, 320, 256,
    256, 128, 160, 128, 192, 128, 160, 128, 1024, 512, 640, 512, 768, 512, 640, 512,
  ];

  /// <summary>
  /// Copy-length floors by the record hit rate (four times the hits over the updates): the first
  /// sixteen for repeated distances (plus 3), the next sixteen for the others (plus 7).
  /// </summary>
  private static readonly byte[] LengthFloor = [
    0, 0, 0, 0, 2, 4, 6, 8, 8, 8, 8, 8, 8, 8, 8, 8,
    1, 1, 1, 1, 2, 3, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5,
  ];

  private readonly Uharc03RangeDecoder _coder;
  private readonly byte[] _window;
  private readonly uint _mask;
  private readonly uint _windowSize;
  private uint _position;

  /// <summary>0 = text stream, 1 = binary stream.</summary>
  private int _kind;
  private long _textEnd = long.MaxValue;
  private long _transformEnd;
  private long _recordStart = uint.MaxValue;

  /// <summary>The stride at which the binary predictor reads record fields; 0 for none.</summary>
  private uint _recordStride;

  private readonly UharcPpmTextTransform _transform = new();

  // Hashed copy prediction.
  private readonly uint[] _hashPosition = new uint[HashSize];
  private readonly uint[] _hashContext = new uint[HashSize];
  private readonly uint[] _deepNewest = new uint[HashSize];
  private readonly uint[] _deepPrevious = new uint[HashSize];
  private readonly byte[] _hashLength = new byte[HashSize];
  private uint _lastDistance = NoPosition;
  private bool _hasExcluded;
  private uint _excludedPosition;
  private readonly ushort[,] _copyClass = new ushort[CopyContexts, 4];
  private int _copyHistory = 7;

  // Hit statistics shared by the records and the copy stage.
  private uint _statTotal;
  private uint _statRecordHits;
  private uint _statStrongHits;

  // The last twelve symbols.
  private uint _roll0;
  private uint _roll1;
  private uint _roll2;
  private uint _symbols;

  private readonly UharcAlzSideModel _side = new();
  private int _selector;

  public Uharc03PpmModel(Uharc03RangeDecoder coder, int method) {
    this._windowSize = (uint)UharcMethods.DictionarySize03(method);
    this._window = new byte[this._windowSize + 0x100];
    this._mask = this._windowSize - 1;
    this._coder = coder;

    Array.Fill(this._hashPosition, NoPosition);
    Array.Fill(this._hashContext, NoPosition);
    Array.Fill(this._deepNewest, NoPosition);
    Array.Fill(this._deepPrevious, NoPosition);
    this._escape.AsSpan(0x203).Fill(1);
  }

  /// <summary>Applies the record-file model (filters bit 6) to the first file of the record size.</summary>
  public void UseRecordModel(IReadOnlyList<long> fileEnds) {
    if (fileEnds.Count == 0)
      return;

    long previous = 0;
    foreach (var end in fileEnds) {
      if (end - previous == RecordFileSize)
        break;
      previous = end;
    }

    this._recordStart = previous;
    this._recordStride = previous == 0 ? 10u : 0u;
  }

  // ── Stream header and reset points ─────────────────────────────────────

  private void ReadStreamKind() {
    this._kind = (int)this._coder.DecodeUniform(2);
    if (this._kind != 0)
      return;

    long total = 0;
    var shift = 0;
    do {
      total += (long)this._coder.DecodeUniform(0x800) << shift;
      shift += 11;
    } while (this._coder.DecodeUniform(2) != 0 && shift < 64);

    this._textEnd = total & 0xFFFFFFFF;
    this._transformEnd = this._textEnd;
  }

  public void Run(byte[] output, IReadOnlyList<long> fileEnds) {
    this.ReadStreamKind();
    var reset = new ResetPoints(fileEnds, output.Length);
    var written = 0;
    while (written < output.Length) {
      if (written >= this._textEnd) {
        this.SwitchToBinary();
        reset.Next = written;
      }

      if (written >= reset.Next)
        this.ResetBlock(reset, written);

      written = this.TryCopy(output, written, out var copied) ? copied : this.DecodeLiteral(output, written);
    }
  }

  /// <summary>
  /// Model resets: from the last reset point the coder looks 256 KiB (text) or 64 KiB (binary)
  /// ahead and resets at the first file end at or beyond that, but stops early at a file end
  /// that reaches the end of the text or the start of the record file.
  /// </summary>
  private sealed class ResetPoints(IReadOnlyList<long> ends, long total) {
    public int File { get; private set; }
    public long Next;

    public IReadOnlyList<long> Ends => ends;

    public void Advance(Uharc03PpmModel model) {
      var span = 0x40000L >> (model._kind * 2);
      var target = total - this.Next > span ? this.Next + span : total;
      while (this.File < ends.Count && ends[this.File] < target) {
        ++this.File;
        if (this.File < ends.Count && (ends[this.File] >= model._textEnd || ends[this.File] >= model._recordStart))
          break;
      }

      this.Next = this.File < ends.Count ? ends[this.File] : total;
    }
  }

  /// <summary>
  /// The reset at a reset point: count pairs back to one, a fresh binary predictor, cleared
  /// order-1 and order-0 rows, halved class and length rows, uniform fallbacks, quartered order-2
  /// records, cleared stride and cost state; then the file-size rules and the multimedia class.
  /// </summary>
  private void ResetBlock(ResetPoints reset, long written) {
    Array.Fill(this._pairs, (byte)1);
    foreach (var order in this._ring)
      Array.Clear(order);

    Array.Clear(this._count, 0, 0x203 * 256);
    Array.Clear(this._rowTotal, 0, 0x203);
    for (var row = LiteralFallback; row < Rows; ++row) {
      this._count.AsSpan(row * 256, 256).Fill(8);
      this._rowTotal[row] = 0x800;
    }

    this._escape.AsSpan(0, 0x203).Fill(1);
    this.SeedCopyClasses();
    for (var row = 0x203; row < LiteralFallback; ++row)
      this.RescaleRow(row);
    this.QuarterOrder2();

    this._strideEvents = 0;
    this._o2Novelty = 0;
    this._ringCredit = 0;
    this._rankStep = 0x1FF;
    this._rankPointer = 0;
    Array.Clear(this._rank);
    Array.Fill(this._lastAt, NoPosition);
    Array.Clear(this._distanceHits);
    this.InitializeStrideList();
    Array.Fill(this._ageRing, UniformCost);
    this._ageSlot = 0xFF;
    this._ageTotal = RawCost;
    Array.Clear(this._costOrderZero);
    Array.Clear(this._costRow);
    this._wpA = 0;
    this._wpB = 0;

    reset.Advance(this);
    this.ApplyFileSizeRules(reset, written);
    this.SelectClass((int)this._coder.DecodeUniform(10));
  }

  /// <summary>
  /// Size-keyed adjustments at a reset point (docs/UHARC-ON-DISK.md, "UHARC 0.3 PPM", Resets): by
  /// the sizes of the file the new block ends with and of the file before it.
  /// </summary>
  private void ApplyFileSizeRules(ResetPoints reset, long written) {
    var file = reset.File;
    if (file == 0 || file >= reset.Ends.Count)
      return;

    var ends = reset.Ends;
    var before = ends[file - 1];
    var current = reset.Next - before;
    var previous = file > 1 ? before - ends[file - 2] : before;
    if ((previous, current) is (0x75A45, 0x68302) or (0x8F4D0, 0x2D9A22))
      Array.Clear(this._o4);
    if ((previous, current) is (0x17E5F8, 0x2CC640) or (0x2CC640, 0x6AF80)) {
      this.QuarterOrder4();
      this.QuarterOrder2();
    }

    var recordFile = previous != RecordFileSize && current == RecordFileSize && written >= this._recordStart;
    if ((previous, current) is (0x388DC2, 0x16DD2E) or (0x7D4C0, 0x3C41E) || recordFile) {
      Array.Clear(this._o4);
      Array.Clear(this._o2);
      if (written >= this._recordStart)
        this._recordStride = 10;
    }
  }

  /// <summary>
  /// Where a text stream ends: the window is rewritten as the bytes the transform produced, the
  /// records are quartered, the hashes and the order-4 records are rebuilt over the last 32 KiB,
  /// the copy-length rows are cleared and coding continues as a binary stream.
  /// </summary>
  private void SwitchToBinary() {
    var (total, recordHits, strongHits) = (this._statTotal, this._statRecordHits, this._statStrongHits);
    var mask = this._mask;
    var cursor = this._position;
    Span<byte> ahead = stackalloc byte[256];
    for (var i = 0; i < 256; ++i)
      ahead[i] = this._window[(cursor + (uint)i) & mask];

    var start = (cursor + 0x140) & mask;
    var half = (this._windowSize - 0x140) >> 1;
    var read = half > this._symbols ? 0 : (start + half) & mask;
    var write = start;
    var pending = 0;
    var escapeKind = 0;
    if (read != cursor)
      while (true) {
        var value = (byte)~this._window[read];
        if (pending == 1) {
          pending = 0;
          this.Rewrite(ref write, (byte)~UharcPpmTables.TransformEscape[((escapeKind - 0xA6) * 11 + value) & 0x7FFFFFFF]);
        } else if (value < 0x80 && pending == 0)
          this.Rewrite(ref write, (byte)~value);
        else {
          var kind = UharcPpmTables.TransformClass[value];
          if (kind >= 0xC8) {
            this.Rewrite(ref write, (byte)~(value - pending));
            pending = 0;
          } else if (kind == 0x64) {
            pending = 1;
            escapeKind = value;
          } else if (kind == 0x63)
            pending = 0x20;
          else {
            var offset = (int)UharcPpmTables.TransformStringOffset[kind];
            var length = (int)UharcPpmTables.TransformStringLength[kind];
            if (pending != 0) {
              // The capitalised first letter is written without looking at where the reading is.
              this.Rewrite(ref write, (byte)(UharcPpmTables.TransformStrings[offset++] + 0x20));
              --length;
              pending = 0;
            }

            do {
              this.Rewrite(ref write, UharcPpmTables.TransformStrings[offset++]);
              if (write == read)
                break;
            } while ((--length & 0xFF) != 0);
          }
        }

        if (write == read)
          break;
        read = (read + 1) & mask;
        if (read == cursor)
          break;
      }

    this._position = write;
    for (var i = 0; i < 256; ++i)
      this._window[(write + (uint)i) & mask] = ahead[i];
    for (var i = 1u; i <= 0x40; ++i)
      this._window[(start - i) & mask] = 0;
    Array.Copy(this._window, 0, this._window, this._windowSize, 0x100);

    this.QuarterOrder4();
    this.QuarterOrder2();
    Array.Fill(this._hashPosition, NoPosition);
    Array.Fill(this._hashContext, NoPosition);
    Array.Fill(this._deepNewest, NoPosition);
    Array.Fill(this._deepPrevious, NoPosition);
    this._roll0 = this._roll1 = this._roll2 = 0;
    this._o4Charge = false;
    this._o4Used = false;

    var warm = start;
    var stop = NoPosition;
    if (((this._position + this._windowSize - start) & mask) > 0x8000)
      stop = (this._position - 0x8000) & mask;
    while (warm != this._position) {
      var hash = HashOf(this._roll0);
      var deep = this.DeepHashOf();
      this._deepPrevious[deep] = this._deepNewest[deep];
      this._deepNewest[deep] = warm;
      this._hashPosition[hash] = warm;
      this._hashContext[hash] = this._roll0;
      if (warm == stop)
        stop = NoPosition;
      if (stop == NoPosition) {
        this._context = this._roll0;
        this.FindOrder4();
        this.UpdateOrder4(this._window[warm]);
      }

      this.Roll(this._window[warm]);
      warm = (warm + 1) & mask;
    }

    (this._statTotal, this._statRecordHits, this._statStrongHits) = (total, recordHits, strongHits);
    Array.Clear(this._count, 0x2FB * 256, 2 * 256);
    Array.Clear(this._rowTotal, 0x2FB, 2);
    this._escape.AsSpan(0x2FB, 2).Fill(1);
    this._textEnd = long.MaxValue;
    this._kind = 1;
  }

  private void Rewrite(ref uint write, byte symbol) {
    this._window[write] = symbol;
    write = (write + 1) & this._mask;
  }

  // ── Copies ─────────────────────────────────────────────────────────────

  private static int HashOf(uint context) => (int)(((context >> 13) ^ context) & 0x3FFFF);

  private int DeepHashOf() => (int)((((this._roll0 >> 15) ^ this._roll0)
    ^ (((this._roll1 >> 15) ^ this._roll1) << 4)
    ^ (((this._roll2 >> 15) ^ this._roll2) << 6)) & 0x3FFFF);

  /// <summary>Predicts a copy and, when the stream takes it, writes it; false for a literal.</summary>
  private bool TryCopy(byte[] output, int written, out int after) {
    after = written;
    var position = this._position;
    var hash = HashOf(this._roll0);
    var deep = this.DeepHashOf();

    // The longest agreement (up to 64 bytes) of the two deep-context candidates with the bytes
    // just written; the later candidate wins ties.
    uint best = 0, candidate = 0;
    var current = position;
    while (current < 0x40)
      current += this._windowSize;
    for (var depth = 0; depth < 2; ++depth) {
      var other = depth == 0 ? this._deepPrevious[deep] : this._deepNewest[deep];
      if (other == NoPosition || ((other - position) & this._mask) < 0x140)
        continue;

      var o = other < 0x40 ? other + this._windowSize : other;
      if (this._window[current - 0xC] != this._window[o - 0xC])
        continue;

      uint n = 0;
      uint a = current - 1, b = o - 1;
      while (n < 0x40 && this._window[a] == this._window[b]) {
        ++n;
        --a;
        --b;
      }

      if (n >= best) {
        best = n;
        candidate = other;
      }
    }

    this._deepPrevious[deep] = this._deepNewest[deep];
    this._deepNewest[deep] = position;

    // Kinds: 0x11 a deep match, 9 the last distance in an agreeing context, 1 the hashed
    // position (or, when the hash slot belongs to another context, the last distance anyway).
    int kind;
    var repeated = false;
    if (best >= 0xC)
      kind = 0x11;
    else {
      candidate = (position - this._lastDistance) & this._mask;
      if (((candidate - position) & this._mask) >= 0x104 && this.ContextAgrees(candidate)) {
        kind = 9;
        repeated = true;
      } else {
        if (this._roll0 == this._hashContext[hash])
          candidate = this._hashPosition[hash];
        kind = candidate != NoPosition ? 1 : 0;
      }
    }

    this._hashPosition[hash] = position;
    this._hashContext[hash] = this._roll0;
    if (kind == 0 || ((candidate - position) & this._mask) < 0x100)
      return false;

    kind += this._hashLength[hash] == 0 ? 0x17 : -1;
    if (this._hasExcluded && this._window[this._excludedPosition] == this._window[candidate]) {
      this.CountCopyClass(kind, 2);
      this._hashLength[hash] = 0;
      return false;
    }

    if (this.DecodeCopyClass(kind) != 1) {
      this._hashLength[hash] = 0;
      return false;
    }

    var length = this.DecodeCopyLength(hash, repeated);
    this._lastDistance = (position - candidate) & this._mask;
    this._hasExcluded = false;
    this._hashLength[hash] = (byte)length;
    if (length is >= 7 and < 0xFF) {
      this._excludedPosition = (candidate + (uint)length) & this._mask;
      this._hasExcluded = true;
    }

    // Bytes are read as the copy proceeds, so an overlapping copy repeats what it just produced.
    // The count runs down as a byte, so length 0 copies 256 bytes, and only its first one is
    // already "within the last eight".
    var remaining = (byte)length;
    var source = candidate;
    do {
      var value = this._window[source];
      written = this.Emit(output, written, value);
      this.Upkeep(value, remaining <= 8);
      this.Store(value);
      source = (source + 1) & this._mask;
    } while (--remaining != 0);

    after = written;
    return true;
  }

  private bool ContextAgrees(uint candidate) {
    var b = (candidate - 4) & this._mask;
    var before = (uint)this._window[b + 3] | (uint)this._window[b + 2] << 8 | (uint)this._window[b + 1] << 16 | (uint)this._window[b] << 24;
    var now = this._roll0;
    return (before & 0xFFFF) == (now & 0xFFFF)
      || (before & 0xFFFF00FF) == (now & 0xFFFF00FF)
      || (before & 0xFFFFFF00) == (now & 0xFFFFFF00);
  }

  private void SeedCopyClasses() {
    for (var i = 0; i < CopyContexts; ++i) {
      this._copyClass[i, 0] = 0;
      this._copyClass[i, 1] = 1;
      this._copyClass[i, 2] = CopySeed[i];
      this._copyClass[i, 3] = 0x1000;
    }
  }

  /// <summary>Hit (1), miss (2), or — escape — a new multimedia class followed by a retry.</summary>
  private int DecodeCopyClass(int kind) {
    while (true) {
      var context = this._copyHistory + kind;
      var count = this._coder.GetCount(this._copyClass[context, 3]);
      var symbol = 2;
      while (symbol > 0 && count < this._copyClass[context, symbol])
        --symbol;
      this._coder.Consume(this._copyClass[context, symbol], (uint)(this._copyClass[context, symbol + 1] - this._copyClass[context, symbol]));
      if (symbol != 0) {
        this.CountCopyClass(kind, symbol);
        return symbol;
      }

      this.SelectClass((int)this._coder.DecodeUniform(10));
    }
  }

  private void CountCopyClass(int kind, int decodedClass) {
    var context = this._copyHistory + kind;
    if (decodedClass == 1)
      this._copyClass[context, 2] += 0x40;
    var total = (ushort)(this._copyClass[context, 3] + 0x40);
    this._copyClass[context, 3] = total;
    if (total >= 0x4000) {
      uint low = this._copyClass[context, 1];
      uint hit = this._copyClass[context, 2];
      var a = ((hit - low + 1) >> 1) + low;
      var b = ((uint)total - hit + 1) >> 1;
      this._copyClass[context, 2] = (ushort)a;
      this._copyClass[context, 3] = (ushort)(a + b);
    }

    this._copyHistory = (this._copyHistory * 2 + decodedClass - 1) & 7;
  }

  /// <summary>
  /// A copy length is a symbol of one of two rows (the second when the hash slot remembers a
  /// long copy) with the uniform row 0x30A behind them. The lengths from 7 up to the floor, and 3
  /// outside repeated distances, are excluded.
  /// </summary>
  private int DecodeCopyLength(int hash, bool repeated) {
    this._excludedCount = 0;
    int floor;
    if (repeated)
      floor = this._statTotal == 0 ? 3 : LengthFloor[(this._statRecordHits << 2) / this._statTotal] + 3;
    else {
      this._excluded[this._excludedCount++] = 3;
      floor = this._statTotal == 0 ? 7 : (LengthFloor[16 + (this._statRecordHits << 2) / this._statTotal] + 7 - this._kind) & 0xFF;
      if (this._statStrongHits > this._statTotal)
        floor = 0x1D;
      else if (this._markerState != 0)
        floor = (floor + floor) & 0xFF;
    }

    for (var f = floor; f > 7; --f)
      this._excluded[this._excludedCount++] = (byte)(f - 1);

    var row = 0x2FB + (!repeated && this._hashLength[hash] >= 0x10 ? 1 : 0);
    this._fallback = 0x30A;
    var length = this.DecodeRow(row);
    this.CountSymbol(length, row);
    return length;
  }

  // ── Emitting a symbol ──────────────────────────────────────────────────

  private int Emit(byte[] output, int written, byte symbol)
    => this._transform.Emit(symbol, written, this._transformEnd, this._recordStart, output);

  private void Store(byte value) {
    this._window[this._position] = value;
    if (this._position < 0x100)
      this._window[this._position + this._windowSize] = value;
    this._position = (this._position + 1) & this._mask;
  }

  private void Roll(byte value) {
    this._roll2 = (this._roll2 << 8) | (this._roll1 >> 24);
    this._roll1 = (this._roll1 << 8) | (this._roll0 >> 24);
    this._roll0 = (this._roll0 << 8) | value;
  }

  private void SelectClass(int selector) {
    if (selector > 9)
      throw new InvalidDataException("UHARC: PPM class out of range.");
    if (selector == this._selector)
      return;

    this._selector = selector;
    this._side.Select(selector);
  }

  /// <summary>The class row in this model's numbering (the ALZ side model's less 0xFE).</summary>
  private int ClassRow => this._side.Context - 0xFE;
}
