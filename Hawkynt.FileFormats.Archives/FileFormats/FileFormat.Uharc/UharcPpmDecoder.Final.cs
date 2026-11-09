namespace FileFormat.Uharc;

/// <summary>
/// The last stage of the PPM literal cascade (docs/UHARC-ON-DISK.md, "PPM", Literals step 4):
/// an order-1 frequency row, the order-0 row or a uniform byte, chosen by how well each has
/// been doing lately; in binary streams a linear predictor may code the byte as a difference.
/// </summary>
internal sealed partial class UharcPpmDecoder {

  private const uint AgeLow = 0x6D4250;
  private const uint AgeHigh = 0x91ADC0;
  private const uint AgeCeiling = 0xFEFF01;
  private const uint UniformCost = 0x9240;

  private enum AgePath { Uniform, OrderZeroOrRow, Row }

  private AgePath CurrentAgePath() {
    var age = this._ageTotal - this._ageRing[(this._ageSlot + 1) & 0xFF];
    if (age is < AgeLow or >= AgeCeiling)
      return AgePath.Row;
    return age < AgeHigh ? AgePath.OrderZeroOrRow : AgePath.Uniform;
  }

  private static void Smooth(uint[] table, int flag, uint cost) => table[flag] = cost + ((table[flag] * 31) >> 5);

  private void CommitAge(int slot, uint cost) {
    this._ageSlot = slot;
    this._ageTotal = this._ageTotal - this._ageRing[slot] + cost;
    this._ageRing[slot] = cost;
  }

  private void ShiftExclusions(int bias, bool mirror, bool toDifference) {
    for (var i = 0; i < this._excludedCount; ++i) {
      var e = this._excluded[i];
      this._excluded[i] = mirror ? (byte)(bias - e) : toDifference ? (byte)(e - bias) : (byte)(e + bias);
    }
  }

  private struct RowDecode {
    public int Symbol;
    public int Row;
    public bool Uniform;
    public KernelResult Kernel;
  }

  private RowDecode DecodeRow(int row, int fallback) {
    var result = new RowDecode { Row = row };
    var flag = fallback == LiteralFallback ? 1 : 0;
    switch (this.CurrentAgePath()) {
      case AgePath.Uniform:
        result.Symbol = (int)this._coder.DecodeUniform(0x100);
        result.Uniform = true;
        result.Kernel = new() { ScaleMiss = -1, ScaleHit = -1, Supported = true };
        return result;
      case AgePath.OrderZeroOrRow when this._costContext[flag] > this._costOrderZero[flag]:
        result.Row = OrderZeroRow;
        fallback = 0x302 + flag;
        break;
    }

    this._fallbackRow = fallback;
    result.Symbol = this.KernelDecode(result.Row, ref result.Kernel);
    if (!result.Kernel.Supported)
      throw new InvalidDataException("UHARC: PPM literal outside the model.");
    return result;
  }

  private int DecodeFromContexts(byte[] output, int written, int row, bool gate, bool skipOrder2, out int symbol) {
    var ageSlot = (this._ageSlot + 1) & 0xFF;
    if (this._kind != 0) {
      this._wpKernel = this._wpArmed;
      gate = this.StrideGate();
    }

    var strideBack = this._window[(this._position - this._stride) & this._mask];
    var contextRow = gate ? 0x100 + strideBack : row;
    if (this._kind == 0)
      this._sinceMiss >>= 3;
    if (this._kind == 0 || !skipOrder2)
      this._order2Update = true;

    var classSelected = this.ClassSelected();
    int predictorRow, predictorFallback;
    if (classSelected) {
      predictorRow = this.ClassContext;
      predictorFallback = 0x304 + this._selector;
    } else {
      var delta = (byte)(strideBack - this._window[(this._position - 2 * this._stride) & this._mask]);
      predictorRow = DeltaContext[delta] + 0x201;
      predictorFallback = 0x304;
    }

    var fired = this._kind != 0 && this.LinearPredictorGate();
    var bias = classSelected ? this._side.Bias : strideBack;
    var mirror = classSelected && this._side.Negate;

    RowDecode decoded;
    if (fired) {
      this.ShiftExclusions(bias, mirror, toDifference: true);
      decoded = this.DecodeRow(predictorRow, predictorFallback);
      decoded.Symbol = mirror ? (byte)(bias - decoded.Symbol) : (byte)(decoded.Symbol + bias);
    } else
      decoded = this.DecodeRow(contextRow, LiteralFallback);

    var value = (byte)decoded.Symbol;
    var difference = mirror ? (byte)(bias - value) : (byte)(value - bias);
    symbol = value;
    return this.FinishFromContexts(output, written, row, contextRow, value, decoded, gate, ageSlot,
      predictorRow, difference, fired, bias, predictorFallback, mirror, fired && classSelected);
  }

  private int FinishFromContexts(byte[] output, int written, int row, int contextRow, byte value, RowDecode decoded, bool gate,
    int ageSlot, int predictorRow, byte difference, bool fired, int bias, int predictorFallback, bool mirror, bool armedAfter) {
    int flag, takenRow, takenSymbol, takenFallback, shadowRow, shadowSymbol, shadowFallback, shadowFlag;
    if (fired) {
      flag = 0;
      (takenRow, takenSymbol, takenFallback) = (predictorRow, difference, predictorFallback);
      (shadowRow, shadowSymbol, shadowFallback, shadowFlag) = (contextRow, value, LiteralFallback, 1);
    } else {
      flag = 1;
      (takenRow, takenSymbol, takenFallback) = (contextRow, value, LiteralFallback);
      (shadowRow, shadowSymbol, shadowFallback, shadowFlag) = (predictorRow, difference, predictorFallback, 0);
    }

    var kernel = decoded.Kernel;
    if (kernel.ScaleHit >= 0) {
      var v = this._escapeScale[kernel.ScaleHit];
      this._escapeScale[kernel.ScaleHit] = v + ((0x2000 - v) >> 6);
    }

    var path = this.CurrentAgePath();
    if (this._wpKernel)
      this._wpKernelCost += path == AgePath.Uniform ? UniformCost : kernel.Cost;

    uint ageCost;
    switch (path) {
      case AgePath.Uniform: {
        this._fallbackRow = takenFallback;
        var e = this.EstimateCost(takenRow, takenSymbol);
        this.Apply(e);
        Smooth(this._costOrderZero, flag, e.Cost);
        Smooth(this._costContext, flag, e.Cost);
        ageCost = e.Cost;
        break;
      }
      case AgePath.Row: {
        this._fallbackRow = takenFallback;
        var e = this.EstimateCost(takenRow, takenSymbol);
        this.ScaleDown(kernel.ScaleMiss);
        this.Apply(e);
        Smooth(this._costOrderZero, flag, kernel.Cost);
        Smooth(this._costContext, flag, kernel.Cost);
        ageCost = kernel.Cost;
        break;
      }
      default: {
        Estimate row1, row0;
        uint costRow, costZero;
        if (decoded.Row == takenRow) {
          this._fallbackRow = takenFallback;
          row1 = this.EstimateCost(takenRow, takenSymbol);
          this.ScaleDown(kernel.ScaleMiss);
          this._fallbackRow = 0x302 + flag;
          row0 = this.EstimateCost(OrderZeroRow, takenSymbol);
          costRow = kernel.Cost;
          costZero = row0.Cost;
          this.Apply(row0);
          this.Apply(row1);
        } else {
          this._fallbackRow = 0x302 + flag;
          row0 = this.EstimateCost(OrderZeroRow, takenSymbol);
          this.ScaleDown(kernel.ScaleMiss);
          this._fallbackRow = takenFallback;
          row1 = this.EstimateCost(takenRow, takenSymbol);
          if (this._costContext[flag] <= this._costOrderZero[flag]) {
            costRow = kernel.Cost;
            costZero = row0.Cost;
          } else {
            costRow = row1.Cost;
            costZero = kernel.Cost;
          }

          this.Apply(row1);
          this.Apply(row0);
        }

        Smooth(this._costContext, flag, costRow);
        Smooth(this._costOrderZero, flag, costZero);
        ageCost = kernel.Cost;
        break;
      }
    }

    this.CommitAge(ageSlot, ageCost);

    if (fired)
      this.ShiftExclusions(bias, mirror, toDifference: false);

    if (this._kind != 0) {
      if (!fired)
        this.ShiftExclusions(bias, mirror, toDifference: true);
      this._fallbackRow = shadowFallback;
      var shadow = this.EstimateCost(shadowRow, shadowSymbol);
      this.Apply(shadow);
      this.PrepareClassUpdate(value);
      this._wpPending = armedAfter;
      Smooth(this._costContext, shadowFlag, shadow.Cost);
      Smooth(this._costOrderZero, shadowFlag, shadow.Cost);
      this._fallbackRow = shadowFallback;
      this.Count(shadowSymbol, shadowRow);
      if (!fired)
        this.ShiftExclusions(bias, mirror, toDifference: false);
    }

    this.UpdateOrder4(value, this._o4Index, this._o4Slot);
    if (this._kind == 0 || this._order2Update) {
      var slot = this._o2Slot;
      this.UpdateOrder2(value, light: false);
      if (slot == 0)
        this._o2Slot = 0;
    }

    if (path != AgePath.OrderZeroOrRow) {
      this._fallbackRow = takenFallback;
      this.Count(takenSymbol, takenRow);
    } else
      this.CountLiteral(takenRow, takenFallback, takenSymbol);

    this.Store(value);
    written = this.Emit(output, written, value);
    if (gate)
      this.CountPrediction(row, value);
    this.AfterSymbol(value, true);
    this.Advance();
    return written;
  }

  private void ScaleDown(int index) {
    if (index < 0)
      return;
    var v = this._escapeScale[index];
    this._escapeScale[index] = v - (v >> 6);
  }
}
