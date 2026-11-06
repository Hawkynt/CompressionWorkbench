namespace FileFormat.Uharc;

/// <summary>
/// PPM model set-up (docs/UHARC-ON-DISK.md, "PPM", Resets and Switch to binary): the initial state, the reset at
/// every reset point, and the switch from a text stream to binary coding.
/// </summary>
internal sealed partial class UharcPpmDecoder {

  private void InitializeContexts() {
    for (var row = 0; row <= 0x300; ++row) {
      this._novelty[row] = 1;
      this._budget[row] = 0x4000;
      this._step[row] = (ushort)(row >= 0x2F9 ? 0x0A : 0x20);
      this._fallbackStep[row] = (ushort)(row >= 0x2E9 ? 0x08 : 0x10);
      this._predicted[row] = 0xFF;
      if (row >= 0x209)
        this._escapeWeight[row] = 1;
    }

    this._fallbackStep[OrderZeroRow] = 6;
    this.SeedProbabilities();
  }

  private void SeedProbabilities() {
    for (var c = 0; c < 32; ++c) {
      var bias = (c & 4) + (c >= 16 ? 8 : 0);
      for (var i = 0; i < 16; ++i) {
        var v = (uint)(UharcPpmTables.SeedEscapeRow[i] + bias) << 7;
        this._o2First[c * 16 + i] = v;
        this._o4First[c * 16 + i] = v;
      }

      this._o2Escape[c] = 0x1000;
      this._escapeScale[c] = (uint)UharcPpmTables.SeedScaleRow[c] << 7;
    }

    for (var c = 0; c < 128; ++c) {
      var low = c >= 16 ? 1 : 0;
      var from8 = c >= 8 ? 4 : 0;
      var from16 = c >= 16 ? 4 : 0;
      var from64 = c >= 64 ? 4 : 0;
      for (var i = 0; i < 16; ++i) {
        var j = c * 16 + i;
        var v = UharcPpmTables.SeedSymbolRow[i];
        if (c < 16) {
          this._o2Predicted[j] = (uint)(v + from8) << 7;
          this._o4Order2[j] = (uint)(v + from8) << 7;
        }

        if (c < 32)
          this._o4Predicted[j] = (uint)(v + from16) << 7;
        this._mpsProbability[j] = (uint)(v + from64) << 7;
        this._o2Main[j] = (uint)(v + low + from64) << 7;
        this._o4Main[j] = (uint)(v + low + from64) << 7;
      }
    }

    for (var a = 0; a < 8; ++a) {
      for (var b = 0; b < 8; ++b) {
        var offset = b > a ? UharcPpmTables.SeedEscapeMassStep[b - a] : a > b ? -UharcPpmTables.SeedEscapeMassStep[a - b] : 0;
        for (var i = 0; i < 32; ++i)
          this._o4Escape[(a << 8) + b * 32 + i] = (uint)(0x1000 + offset);
      }

      for (var i = 0; i < 32; ++i)
        this._o2Hit[a * 32 + i] = (uint)UharcPpmTables.SeedHitRow[(i & 1) + a] << 7;
      for (var i = 0; i < 1024; ++i)
        this._o4Hit[a * 1024 + i] = (uint)UharcPpmTables.SeedHitRow[(i & 1) + a] << 7;
      for (var i = 0; i < 256; ++i)
        this._lengthProbability[a * 256 + i] = 0x800;
    }
  }

  /// <summary>The reset at a reset point; ends by reading the multimedia class for the next block.</summary>
  private void ResetBlock() {
    for (var row = 0; row <= 0x208; ++row) {
      this._novelty[row] = (uint)this._kind;
      this._budget[row] = 0x4000;
      this._predicted[row] = 0xFF;
      this._confidence[row] = 0;
      this._frequency.AsSpan(row * 256, 256).Clear();
      this._rowTotal[row] = 0;
      this._escapeWeight[row] = 1;
    }

    for (var row = LiteralFallback; row < Rows; ++row) {
      this._frequency.AsSpan(row * 256, 256).Fill(8);
      this._rowTotal[row] = 0x800;
    }

    Array.Clear(this._copyClass);
    for (var i = 0; i < UharcPpmTables.LzpClassSeed.Length; ++i) {
      this._copyClass[i, 1] = 1;
      this._copyClass[i, 2] = UharcPpmTables.LzpClassSeed[i];
      this._copyClass[i, 3] = 0x63;
    }

    for (var row = 0x209; row <= 0x300; ++row)
      this.RescaleRow(row);
    this.QuarterOrder2();

    this._sinceMiss = 0;
    this._o2Novelty = 0;
    this._ringCredit = 0;
    this._rankSmallPointer = 0;
    this._rankLargePointer = 0;
    this._rankStep = 0x1FF;
    this._rankBoost = false;
    Array.Clear(this._rankSmall);
    Array.Clear(this._rankLarge);
    Array.Clear(this._seenBefore);
    Array.Clear(this._recentAt);
    Array.Clear(this._distanceHits);
    this._strideMisses = 0;
    this._strideCandidate = 0;
    this._stride = 1;
    this._strideLast = 1;
    Array.Fill(this._ageRing, UniformCost);
    this._ageSlot = 0xFF;
    this._ageTotal = AgeHigh;
    Array.Clear(this._costContext);
    Array.Clear(this._costOrderZero);
    this._wpA = 0;
    this._wpB = 0;
    this._wpAdd = 0;
    this._wpPending = false;
    this._o4Charge = false;
    this._wpKernel = false;
    this._excludedCount = 0;
    this._order2Update = false;
    this._o4Slot = 0;
    this._bonus = 0;
    this._o2Slot = 0;
    this._o2Charge = false;
    this._wpCost = 0;

    this.SelectClass((int)this._coder.DecodeUniform(10));
  }

  /// <summary>
  /// Where a text stream ends: the window is rewritten as the bytes the transform produced, the
  /// copy hashes and order-4 records are rebuilt from the last 32 KiB of it, and coding
  /// continues as a binary stream.
  /// </summary>
  private void SwitchToBinary() {
    var (total, hits, order4) = (this._statTotal, this._statHits, this._statOrder4);
    var mask = this._mask;
    var oldPosition = this._position;
    Span<byte> ahead = stackalloc byte[256];
    for (var i = 0; i < 256; ++i)
      ahead[i] = this._window[(oldPosition + (uint)i) & mask];

    var start = (oldPosition + 0x140) & mask;
    var half = (this._windowSize - 0x140) >> 1;
    var read = (start + half) & mask;
    if (half > this._symbols)
      read = 0;

    var write = start;
    var transform = new UharcPpmTextTransform();
    var expanded = new byte[64];
    while (read != oldPosition) {
      var count = transform.Emit(this._window[read], 0, long.MaxValue, expanded);
      var met = false;
      for (var j = 0; j < count; ++j) {
        this._window[write] = (byte)~expanded[j];
        write = (write + 1) & mask;
        if (write == read) {
          met = true;
          break;
        }
      }

      if (met)
        break;
      read = (read + 1) & mask;
    }

    this._position = write;
    for (var i = 0; i < 256; ++i)
      this._window[(write + (uint)i) & mask] = ahead[i];
    for (var i = 1u; i <= 0x40; ++i)
      this._window[(start - i) & mask] = 0;
    Array.Copy(this._window, 0, this._window, this._windowSize, 0x100);

    this.QuarterOrder4();
    Array.Clear(this._o2);
    Array.Fill(this._hashPosition, NoPosition);
    Array.Fill(this._hashContext, NoPosition);
    Array.Fill(this._deepNewest, NoPosition);
    Array.Fill(this._deepPrevious, NoPosition);
    Array.Clear(this._hashLength);
    Array.Clear(this._hashConfidence);
    this._roll0 = this._roll1 = this._roll2 = 0;
    this._o4Charge = false;

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
      if (stop == NoPosition)
        this.WarmOrder4(this._window[warm]);

      this._roll2 = (this._roll2 << 8) | (this._roll1 >> 24);
      this._roll1 = (this._roll1 << 8) | (this._roll0 >> 24);
      this._roll0 = (this._roll0 << 8) | this._window[warm];
      warm = (warm + 1) & mask;
    }

    (this._statTotal, this._statHits, this._statOrder4) = (total, hits, order4);
    this.SeedProbabilities();
    this._textEnd = long.MaxValue;
    this._kind = 1;
  }

  private void WarmOrder4(byte symbol) {
    var context = this._roll0;
    var index = (context ^ (context >> 13)) & 0x7FFF;
    while (index < Order4Records) {
      if (this.O4(index)[0] == 0) {
        index += Order4Records;
        break;
      }

      if (Tag(this.O4(index)) == context)
        break;
      index += 0x8000;
    }

    this._contextWord = context;
    this._o4Index = index;
    if (index < Order4Records) {
      var r = this.O4(index);
      uint sum = r[0];
      for (var i = 1; i <= 12; ++i)
        sum += r[i];
      this._o4Total = sum;
    }

    this.UpdateOrder4(symbol, index, 0xFF);
  }
}
