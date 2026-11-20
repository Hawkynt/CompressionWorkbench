namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.4 PPM decoder (stream methods 24..39, the <c>-mx</c> mode), implemented from
/// docs/UHARC-ON-DISK.md section "UHARC 0.4 PPM". The model is <see cref="Uharc04PpmModel{TCoder}"/>.
/// </summary>
internal static class Uharc04PpmDecoder {

  /// <summary>Decodes <paramref name="unpackedSize"/> bytes of a 0.4 PPM stream.</summary>
  /// <param name="packed">Buffer holding the packed stream.</param>
  /// <param name="offset">Start of the packed stream.</param>
  /// <param name="length">Length of the packed stream.</param>
  /// <param name="method">Stream method byte, 24..39.</param>
  /// <param name="unpackedSize">Number of bytes to produce.</param>
  /// <param name="fileEnds">Cumulative end offsets of the archived files.</param>
  /// <param name="recordModel">The header's record-file flag (filters bit 6).</param>
  public static byte[] Decode(byte[] packed, int offset, int length, int method, int unpackedSize, IReadOnlyList<long> fileEnds, bool recordModel = false) {
    if (method is < UharcMethods.PpmFirst or > UharcMethods.PpmLast)
      throw new ArgumentOutOfRangeException(nameof(method));

    var output = new byte[unpackedSize];
    if (unpackedSize == 0)
      return output;

    var model = new Uharc04PpmModel<UharcDecoding>(new(new UharcRangeDecoder(packed, offset, length)), method);
    if (recordModel)
      model.UseRecordModel(fileEnds);
    model.Run(output, fileEnds);
    return output;
  }
}

/// <summary>
/// The encoder side of the 0.4 PPM stream (docs/UHARC-ON-DISK.md, "Writing UHARC 0.4"): it runs
/// the decoder's <see cref="Uharc04PpmModel{TCoder}"/> over the model symbols of the input
/// (<see cref="UharcPpmSymbols"/>: leading text files as a text stream with the word transform,
/// the rest binary), which then answers every coding decision from the symbol that comes next,
/// with each block's multimedia class (<see cref="UharcMediaClass"/>).
/// </summary>
internal static class Uharc04PpmEncoder {

  /// <summary>Packs <paramref name="data"/> as 0.4 PPM stream <paramref name="method"/> (24..39).</summary>
  public static byte[] Encode(byte[] data, int method, IReadOnlyList<long> fileEnds)
    => UharcPpmSymbols.EncodeSmaller(data, fileEnds, (d, ends, plan) => Encode(d, method, ends, plan));

  /// <summary>Packs <paramref name="data"/> from the given symbols (a text or a binary stream).</summary>
  internal static byte[] Encode(byte[] data, int method, IReadOnlyList<long> fileEnds, UharcPpmSymbols plan) {
    if (method is < UharcMethods.PpmFirst or > UharcMethods.PpmLast)
      throw new ArgumentOutOfRangeException(nameof(method));
    if (data.Length == 0)
      return [];

    var coder = new UharcRangeEncoder();
    var model = new Uharc04PpmModel<UharcEncoding>(new(coder), method) { Source = data, Plan = plan };
    var output = new byte[data.Length];
    model.Run(output, fileEnds);
    if (!output.AsSpan().SequenceEqual(data))
      throw new InvalidOperationException("UHARC: 0.4 PPM encoder lost track of its model.");
    return coder.Finish();
  }
}

/// <summary>
/// The UHARC 0.4 PPM model (docs/UHARC-ON-DISK.md, "UHARC 0.4 PPM"): the ancestor of 0.6's. Every
/// step first tries a copy predicted from hashed contexts; otherwise one literal goes through an
/// order-4 record, an order-2 record, a binary-data predictor of eight sparse contexts, and
/// finally frequency rows. Compared with 0.6 every stage is simpler: records and the binary
/// predictor are coded with plain counts (learned only through a few count-pair tables), the
/// rows have no predicted-byte step, and copy lengths are symbols of four frequency rows.
/// Generic over the coder direction: decoding reads every decision, encoding answers it from
/// <see cref="Plan"/>.
/// </summary>
internal sealed partial class Uharc04PpmModel<TCoder> where TCoder : struct, IUharcCoder {

  private const uint NoPosition = uint.MaxValue;
  private const int HashSize = 0x40000;
  private const int RecordFileSize = 0xFB670;

  private readonly TCoder _coder;
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
  private uint _runCount;
  private bool _hasExcluded;
  private uint _excludedPosition;
  private readonly ushort[,] _copyClass = new ushort[82, 4];
  private int _copyHistory;

  // Hit statistics shared by the records and the copy stage.
  private uint _statTotal = 1;
  private uint _statRecordHits = 1;
  private uint _statStrongHits = 1;

  // The last twelve symbols.
  private uint _roll0;
  private uint _roll1;
  private uint _roll2;
  private uint _symbols;

  private readonly UharcAlzSideModel _side = new();
  private int _selector;

  // Encoding only: the input, the symbols coding it and the next one's index, the step's output
  // position, the model symbol of the literal being coded (and its form in the row being coded),
  // and the copy decision.
  private byte[] _source = [];
  private UharcPpmSymbols _plan = UharcPpmSymbols.Binary([]);
  private int _next;
  private int _written;
  private long _copyLimit;
  private int _target;
  private int _rowTarget;
  private int _copyClassWanted;
  private int _copyLengthWanted;

  /// <summary>The input the encoding instantiation codes (its multimedia classes are read from it).</summary>
  public byte[] Source {
    set => this._source = value;
  }

  /// <summary>The model symbols the encoding instantiation codes; the model chooses copies from them.</summary>
  public UharcPpmSymbols Plan {
    set => this._plan = value;
  }

  public Uharc04PpmModel(TCoder coder, int method) {
    this._windowSize = (uint)UharcMethods.DictionarySize(method);
    this._window = new byte[this._windowSize + 0x100];
    this._mask = this._windowSize - 1;
    this._coder = coder;

    Array.Fill(this._hashPosition, NoPosition);
    Array.Fill(this._hashContext, NoPosition);
    Array.Fill(this._deepNewest, NoPosition);
    Array.Fill(this._deepPrevious, NoPosition);
    this.InitializeRows();
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
    var textLength = TCoder.Encoding ? this._plan.TextLength : 0;
    this._kind = (int)this._coder.Uniform(2, textLength > 0 ? 0u : 1u);
    if (this._kind != 0)
      return;

    long total = 0;
    var shift = 0;
    do {
      total += (long)this._coder.Uniform(0x800, (uint)(textLength >> shift) & 0x7FF) << shift;
      shift += 11;
    } while (this._coder.Uniform(2, textLength >> shift != 0 ? 1u : 0u) != 0 && shift < 64);

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

      this._written = written;
      if (TCoder.Encoding && this._plan.StartOf(this._next) != written)
        throw new InvalidOperationException("UHARC: 0.4 PPM encoder lost track of its symbols.");
      this._copyLimit = Math.Min(reset.Next, output.Length);
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

    public void Advance(Uharc04PpmModel<TCoder> model) {
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
  /// The reset at a reset point: fresh count-pair tables and binary predictor, cleared order-1
  /// and order-0 rows, halved class and length rows, uniform fallbacks, quartered order-2
  /// records, cleared stride and cost state; then the file-size rules and the multimedia class.
  /// </summary>
  private void ResetBlock(ResetPoints reset, long written) {
    this.SeedPairs();
    foreach (var order in this._ring)
      Array.Clear(order);

    Array.Clear(this._count, 0, 0x203 * 256);
    Array.Clear(this._rowTotal, 0, 0x203);
    for (var row = 0x2FF; row < Rows; ++row) {
      this._count.AsSpan(row * 256, 256).Fill(8);
      this._rowTotal[row] = 0x800;
    }

    this._escape.AsSpan(0, 0x203).Fill(1);
    this.SeedCopyClasses();
    for (var row = 0x203; row < 0x2FF; ++row)
      this.RescaleRow(row);
    this.QuarterOrder2();

    this._o2Novelty = 0;
    this._ringCredit = 0;
    this._rankSmallPointer = 0;
    this._rankLargePointer = 0;
    this._rankStep = 0x1FF;
    Array.Clear(this._rankSmall);
    Array.Clear(this._rankLarge);
    Array.Clear(this._lastAt);
    Array.Clear(this._distanceHits);
    this._strideMisses = 0;
    this._stride = 1;
    this._strideLast = 1;
    this._strideCandidate = 0;
    Array.Fill(this._ageRing, UniformCost);
    this._ageSlot = 0xFF;
    this._ageTotal = RawCost;
    Array.Clear(this._costOrderZero);
    Array.Clear(this._costRow);
    this._wpA = 0;
    this._wpB = 0;

    reset.Advance(this);
    this.ApplyFileSizeRules(reset, written);
    this.SelectClass((int)this._coder.Uniform(10, TCoder.Encoding ? (uint)UharcMediaClass.ForBlock(this._source, reset.Ends, written, reset.Next) : 0u));
  }

  /// <summary>
  /// Size-keyed adjustments at a reset point (docs/UHARC-ON-DISK.md, "UHARC 0.4 PPM", Resets): by
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
      Array.Clear(this._o4, 0, 0x8000 * Order4Stride);
    if ((previous, current) is (0x17E5F8, 0x2CC640) or (0x2CC640, 0x6AF80)) {
      this.QuarterOrder4();
      this.QuarterOrder2();
    }

    var recordFile = previous != RecordFileSize && current == RecordFileSize && written >= this._recordStart;
    if ((previous, current) is (0x388DC2, 0x16DD2E) or (0x7D4C0, 0x3C41E) || recordFile) {
      Array.Clear(this._o4, 0, 0x8000 * Order4Stride);
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
    Array.Clear(this._count, 0x2FB * 256, 4 * 256);
    Array.Clear(this._rowTotal, 0x2FB, 4);
    this._escape.AsSpan(0x2FB, 4).Fill(1);
    this._textEnd = long.MaxValue;
    this._kind = 1;
  }

  private void Rewrite(ref uint write, byte symbol) {
    this._window[write] = symbol;
    write = (write + 1) & this._mask;
  }

  // ── Copies ─────────────────────────────────────────────────────────────

  private static int HashOf(uint context) => (int)(((context >> 13) ^ context) & 0x3FFFF);

  private int DeepHashOf() => (int)(((((this._roll0 >> 15) ^ this._roll0) << 4)
    ^ (((this._roll1 >> 15) ^ this._roll1) << 6)
    ^ ((this._roll2 >> 15) ^ this._roll2)) & 0x3FFFF);

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

    var kind = best >= 0xC ? 0x15 : 0;
    var repeated = false;
    var inRun = this._runCount != 0;
    if (inRun) {
      candidate = (position - this._lastDistance) & this._mask;
      kind = this._runCount > 2 ? 0x51 : 0x50;
      repeated = true;
    } else if (kind == 0) {
      candidate = (position - this._lastDistance) & this._mask;
      if (((candidate - position) & this._mask) >= 0x104 && this.ContextAgrees(candidate)) {
        kind = 0x0B;
        repeated = true;
      } else if (this._roll0 == this._hashContext[hash]) {
        candidate = this._hashPosition[hash];
        kind = candidate != NoPosition ? 1 : 0;
      } else
        kind = 0x1F;
    }

    this._hashPosition[hash] = position;
    this._hashContext[hash] = this._roll0;
    if (kind == 0 || ((candidate - position) & this._mask) < 0x100) {
      this._runCount = 0;
      return false;
    }

    if (!inRun)
      kind = (kind + this._copyHistory + (this._hashLength[hash] == 0 ? 0x27 : -1)) & 0xFF;

    if (this._hasExcluded && this._window[this._excludedPosition] == this._window[candidate]) {
      this.CountCopyClass(kind, 2);
      this._hashLength[hash] = 0;
      this._runCount = 0;
      return false;
    }

    var floor = this.CopyFloor(repeated);
    if (TCoder.Encoding)
      this.ChooseCopy(candidate, repeated, floor);
    if (this.DecodeCopyClass(kind) != 1) {
      this._hashLength[hash] = 0;
      this._runCount = 0;
      return false;
    }

    var length = this.DecodeCopyLength(hash, repeated, floor);
    this._lastDistance = (position - candidate) & this._mask;
    this._hasExcluded = false;
    this._hashLength[hash] = (byte)length;
    if (length == 0xFF)
      ++this._runCount;
    else {
      if (length >= 7) {
        this._excludedPosition = (candidate + (uint)length) & this._mask;
        this._hasExcluded = true;
      }

      this._runCount = 0;
    }

    // Bytes are read as the copy proceeds, so an overlapping copy repeats what it just produced.
    var count = length == 0 ? 0x100 : length;
    var source = candidate;
    for (var remaining = count; remaining > 0; --remaining) {
      var value = this._window[source];
      written = this.Emit(output, written, value);
      this.Upkeep(value, remaining <= 8);
      this.Store(value);
      source = (source + 1) & this._mask;
    }

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
    for (var i = 0; i < 82; ++i) {
      this._copyClass[i, 0] = 0;
      this._copyClass[i, 1] = 1;
      this._copyClass[i, 2] = UharcPpmTables.LzpClassSeed[i];
      this._copyClass[i, 3] = 0x63;
    }
  }

  /// <summary>Hit (1), miss (2), or — escape — a new multimedia class followed by a retry.</summary>
  private int DecodeCopyClass(int context) {
    while (true) {
      var count = this._coder.Count(this._copyClass[context, 3], TCoder.Encoding ? this._copyClass[context, this._copyClassWanted] : 0u);
      var symbol = 2;
      while (symbol > 0 && count < this._copyClass[context, symbol])
        --symbol;
      this._coder.Consume(this._copyClass[context, symbol], (uint)(this._copyClass[context, symbol + 1] - this._copyClass[context, symbol]));
      if (symbol != 0) {
        this.CountCopyClass(context, symbol);
        return symbol;
      }

      this.SelectClass((int)this._coder.Uniform(10, 0));
    }
  }

  private void CountCopyClass(int context, int decodedClass) {
    if (decodedClass == 1)
      this._copyClass[context, 2] += 0x20;
    var total = (ushort)(this._copyClass[context, 3] + 0x20);
    this._copyClass[context, 3] = total;
    if (total >= 0x4000) {
      uint low = this._copyClass[context, 1];
      uint hit = this._copyClass[context, 2];
      var a = ((hit - low + 1) >> 1) + low;
      var b = ((uint)total - hit + 1) >> 1;
      this._copyClass[context, 2] = (ushort)a;
      this._copyClass[context, 3] = (ushort)(a + b);
    }

    this._copyHistory = UharcPpmTables.LzpHistoryNext[this._copyHistory * 2 + decodedClass];
  }

  /// <summary>
  /// A copy length is a symbol of one of four rows (by the run count, or by the length the hash
  /// slot remembers) with the uniform row 0x30C behind them. The lengths from 7 up to the
  /// floor, and 3 outside runs and repeated distances, are excluded.
  /// </summary>
  private int DecodeCopyLength(int hash, bool repeated, int floor) {
    this._excludedCount = 0;
    if (!repeated)
      this._excluded[this._excludedCount++] = 3;
    for (var f = floor; f > 7; --f)
      this._excluded[this._excludedCount++] = (byte)(f - 1);

    int row;
    if (this._runCount != 0)
      row = 0x2FD + (this._runCount > 2 ? 1 : 0);
    else
      row = 0x2FB + (!repeated && this._hashLength[hash] >= 0x10 ? 1 : 0);

    this._fallback = 0x30C;
    this._rowTarget = this._copyLengthWanted;
    var length = this.DecodeRow(row);
    this.CountSymbol(length, row);
    return length;
  }

  /// <summary>The copy-length floor: lengths from 7 up to it are not coded.</summary>
  private int CopyFloor(bool repeated) {
    var quotient = (this._statRecordHits << 2) / this._statTotal;
    if (repeated)
      return UharcPpmTables.LengthFloorInRun[quotient];
    if (this._statStrongHits > this._statTotal)
      return 0x1D;

    var floor = (UharcPpmTables.LengthFloorDefault[quotient] - this._kind) & 0xFF;
    return this._markerState != 0 ? (floor + floor) & 0xFF : floor;
  }

  /// <summary>
  /// The encoder's copy decision: the length the candidate reproduces of the input (at most 255,
  /// never past the next reset point), cut to a length that can be coded — not 3 outside a
  /// repeated distance, nothing from 7 below the floor — and taken from 4 bytes on. A copy of 7
  /// to 254 excludes the byte after it from the next literal, so it must be whole: the match
  /// ends there; shorter copies and 255 (a run goes on) exclude nothing.
  /// </summary>
  private void ChooseCopy(uint source, bool repeated, int floor) {
    var position = this._position;
    var symbols = this._plan.Symbols;
    var next = this._next;
    var available = Math.Min(0xFF, this._plan.CopyEnd(this._copyLimit, this._textEnd) - next);
    Span<byte> bytes = stackalloc byte[0x100];
    var length = 0;
    for (; length < available; ++length) {
      var behind = (source - position) & this._mask;
      var value = behind < (uint)length ? bytes[(int)behind] : this._window[source];
      if (value != symbols[next + length])
        break;
      bytes[length] = value;
      source = (source + 1) & this._mask;
    }

    var whole = length < available || next + length >= symbols.Length;
    if (!whole) {
      var behind = (source - position) & this._mask;
      var following = behind < (uint)length ? bytes[(int)behind] : this._window[source];
      whole = following != symbols[next + length];
    }

    if (length is >= 7 and < 0xFF && (!whole || length < floor))
      length = 6;
    if (length == 3 && !repeated)
      length = 2;

    var hit = length >= 4;
    this._copyClassWanted = hit ? 1 : 2;
    this._copyLengthWanted = hit ? length : 0;
  }

  // ── Emitting a symbol ──────────────────────────────────────────────────

  private int Emit(byte[] output, int written, byte symbol) {
    if (TCoder.Encoding)
      ++this._next;
    return this._transform.Emit(symbol, written, this._transformEnd, this._recordStart, output);
  }

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
