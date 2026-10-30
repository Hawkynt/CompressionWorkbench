// ACE 2.0 PIC mode, converted from acefile (https://github.com/droe/acefile, BSD-2-Clause,
// Copyright (c) 2017-2026 Daniel Roethlisberger and contributors). See THIRD-PARTY-NOTICE.acefile.txt.

namespace Compression.Core.Dictionary.Ace;

/// <summary>ACE 2.0 PIC decoding: a two-dimensional pixel predictor over Golomb-Rice residuals.</summary>
internal sealed class AcePic {
  private const int ContextCount = 365;

  private static readonly int[] DifBitWidth = BuildDifBitWidth();
  private static readonly int[] Quantizer = BuildQuantizer(1);
  private static readonly int[] Quantizer9 = BuildQuantizer(9);
  private static readonly int[] Quantizer81 = BuildQuantizer(81);

  private static int BitLength(int v) => v <= 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)v);

  private static int[] BuildDifBitWidth() {
    // indexed by a signed byte value as Python indexes a list with a negative number
    var t = new int[256];
    for (var i = 0; i < 128; ++i)
      t[i] = BitLength(2 * i);
    for (var i = -128; i < 0; ++i)
      t[256 + i] = BitLength(-2 * i - 1);
    return t;
  }

  private static int[] BuildQuantizer(int factor) {
    var q = new List<int>(511);
    for (var i = -255; i < -20; ++i) q.Add(-4);
    for (var i = -20; i < -6; ++i) q.Add(-3);
    for (var i = -6; i < -2; ++i) q.Add(-2);
    for (var i = -2; i < 0; ++i) q.Add(-1);
    q.Add(0);
    for (var i = 1; i < 3; ++i) q.Add(1);
    for (var i = 3; i < 7; ++i) q.Add(2);
    for (var i = 7; i < 21; ++i) q.Add(3);
    for (var i = 21; i < 256; ++i) q.Add(4);
    return [.. q.Select(v => v * factor)];
  }

  private static int UChar(int i) => i & 0xFF;

  /// <summary>Python-style index into a 511-entry or 256-entry table (negative wraps from the end).</summary>
  private static int At(int[] table, int index) => table[index < 0 ? table.Length + index : index];

  private sealed class ErrContext {
    public int UsedCounter;
    public int PredictorNumber;
    public int AverageCounter = 4;
    public readonly int[] ErrorCounters = new int[4];
  }

  private sealed class ErrModel {
    public readonly ErrContext[] Contexts = [.. Enumerable.Range(0, ContextCount).Select(_ => new ErrContext())];
  }

  private sealed class PixelDecoder {
    private readonly int _kind;
    private int _a, _b, _c, _d, _x;

    public PixelDecoder(int kind) {
      this._kind = kind;
      if (kind != 0)
        this._a = this._b = this._c = this._x = 128;
    }

    public static PixelDecoder ReadFrom(AceBitStream bs) {
      var kind = (int)bs.ReadBits(2);
      if (kind > 2)
        throw new InvalidDataException("ACE PIC: unknown pixel decoder.");
      return new PixelDecoder(kind);
    }

    public void ShiftPixels() {
      this._c = this._a;
      this._a = this._d;
      this._b = this._x;
    }

    public int GetContext() {
      var ctx = Quantizer81[255 + this._d - this._a] + Quantizer9[255 + this._a - this._c] + Quantizer[255 + this._c - this._b];
      return Math.Abs(ctx);
    }

    private int Predict(int predictor) => predictor switch {
      0 => this._a,
      1 => this._b,
      2 => (this._a + this._b) >> 1,
      _ => UChar(this._a + this._b - this._c),
    };

    public void UpdatePixelX(AceBitStream bs, ErrContext context) {
      ++context.UsedCounter;
      var r = context.AverageCounter / context.UsedCounter; // both positive: C and Python agree
      var epsilon = bs.ReadGolombRice(BitLength(r), signed: true);
      this._x = UChar(this.Predict(context.PredictorNumber) + epsilon);

      context.AverageCounter += Math.Abs(epsilon);
      if (context.UsedCounter == 128) {
        context.UsedCounter >>= 1;
        context.AverageCounter >>= 1;
      }

      var best = 0;
      for (var i = 0; i < 4; ++i) {
        context.ErrorCounters[i] += At(DifBitWidth, this._x - this.Predict(i));
        if (i == 0 || context.ErrorCounters[i] < context.ErrorCounters[best])
          best = i;
      }
      context.PredictorNumber = best;
      if (context.ErrorCounters.Any(ec => ec > 0x7F))
        for (var i = 0; i < 4; ++i)
          context.ErrorCounters[i] >>= 1;
    }

    public void UpdatePixelD(int thisPlaneD, int refPlaneD)
      => this._d = this._kind switch {
        0 => thisPlaneD,
        1 => UChar(128 + thisPlaneD - refPlaneD),
        _ => UChar(128 + thisPlaneD - (refPlaneD * 11 >> 4)),
      };

    public int Produce(int refPlaneX) => this._kind switch {
      0 => this._x,
      1 => UChar(this._x + refPlaneX - 128),
      _ => UChar(this._x + (refPlaneX * 11 >> 4) - 128),
    };
  }

  private int _width;
  private int _planes;
  private ErrModel _plane0 = new();
  private ErrModel _plane1ToN = new();
  private int[] _prevRow = [];
  private readonly List<byte> _leftover = [];

  public void Reinit(AceBitStream bs) {
    this._width = bs.ReadGolombRice(12);
    this._planes = bs.ReadGolombRice(2);
    this._plane0 = new ErrModel();
    this._plane1ToN = new ErrModel();
    this._prevRow = new int[this._width + this._planes];
    this._leftover.Clear();
  }

  private static int Py(int[] array, int index) => array[index < 0 ? array.Length + index : index];

  private int[] Row(AceBitStream bs) {
    var row = new int[this._width + this._planes];
    for (var plane = 0; plane < this._planes; ++plane) {
      ErrModel model;
      PixelDecoder decoder;
      if (plane == 0) {
        model = this._plane0;
        decoder = new PixelDecoder(0);
      } else {
        model = this._plane1ToN;
        decoder = PixelDecoder.ReadFrom(bs);
      }
      decoder.UpdatePixelD(this._prevRow[plane], Py(this._prevRow, plane - 1));
      for (var col = plane; col < this._width; col += this._planes) {
        decoder.ShiftPixels();
        decoder.UpdatePixelD(this._prevRow[col + this._planes], this._prevRow[col + this._planes - 1]);
        var context = model.Contexts[decoder.GetContext()];
        decoder.UpdatePixelX(bs, context);
        row[col] = decoder.Produce(Py(row, col - 1));
      }
    }
    this._prevRow = row;
    return row[..this._width];
  }

  public (byte[] Chunk, AceMode? NextMode) Read(AceBitStream bs, int wantSize) {
    var chunk = new List<byte>(this._leftover);
    this._leftover.Clear();
    AceMode? nextMode = null;
    while (chunk.Count < wantSize) {
      if (bs.ReadBits(1) == 0) {
        nextMode = AceMode.ReadFrom(bs);
        break;
      }
      var data = this.Row(bs);
      var n = Math.Min(wantSize - chunk.Count, data.Length);
      for (var i = 0; i < n; ++i)
        chunk.Add((byte)data[i]);
      for (var i = n; i < data.Length; ++i)
        this._leftover.Add((byte)data[i]);
    }
    return ([.. chunk], nextMode);
  }
}
