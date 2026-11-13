// ACE 2.0 PIC mode, converted from acefile (https://github.com/droe/acefile, BSD-2-Clause,
// Copyright (c) 2017-2026 Daniel Roethlisberger and contributors). See THIRD-PARTY-NOTICE.acefile.txt.
// Encode is our inverse of that decoder.

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

    public ErrModel Clone() {
      var copy = new ErrModel();
      for (var i = 0; i < ContextCount; ++i) {
        var (from, to) = (this.Contexts[i], copy.Contexts[i]);
        to.UsedCounter = from.UsedCounter;
        to.PredictorNumber = from.PredictorNumber;
        to.AverageCounter = from.AverageCounter;
        from.ErrorCounters.CopyTo(to.ErrorCounters, 0);
      }
      return copy;
    }
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
      this.Learn(context, epsilon);
    }

    /// <summary>The inverse of <see cref="UpdatePixelX"/>: codes the residual that makes the pixel <paramref name="x"/>.</summary>
    public void EncodePixelX(AceBitWriter w, ErrContext context, int x) {
      ++context.UsedCounter;
      var r = context.AverageCounter / context.UsedCounter;
      var epsilon = (int)(sbyte)(byte)(x - this.Predict(context.PredictorNumber));
      w.WriteGolombRice(epsilon, BitLength(r), signed: true);
      this._x = x;
      this.Learn(context, epsilon);
    }

    /// <summary>The value the predictor has to reach so that <see cref="Produce"/> yields <paramref name="pixel"/>.</summary>
    public int Target(int pixel, int refPlaneX) => this._kind switch {
      0 => pixel,
      1 => UChar(pixel - refPlaneX + 128),
      _ => UChar(pixel - Scaled(refPlaneX) + 128),
    };

    private void Learn(ErrContext context, int epsilon) {
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

    // Kind 2 scales the reference plane's value as a signed byte: unace 2.5 decodes our streams with
    // reference values of 128 and above exactly that way (and acefile, as of 11ed076a, unsigned). The
    // two readings agree below 128, so the writer keeps kind 2 to rows where every reference is below.
    private static int Scaled(int refPlaneValue) => (sbyte)(byte)refPlaneValue * 11 >> 4;

    public void UpdatePixelD(int thisPlaneD, int refPlaneD)
      => this._d = this._kind switch {
        0 => thisPlaneD,
        1 => UChar(128 + thisPlaneD - refPlaneD),
        _ => UChar(128 + thisPlaneD - Scaled(refPlaneD)),
      };

    public int Produce(int refPlaneX) => this._kind switch {
      0 => this._x,
      1 => UChar(this._x + refPlaneX - 128),
      _ => UChar(this._x + Scaled(refPlaneX) - 128),
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

  /// <summary>
  /// The bits <see cref="Reinit"/> and <see cref="Read"/> decode back to <paramref name="data"/>, a whole
  /// number of <paramref name="width"/>-byte rows with <paramref name="planes"/> interleaved planes: width
  /// and planes, then each row behind a 1 bit. Every plane after the first is coded with whichever of the
  /// three decoder kinds (plain, minus the previous plane, minus 11/16 of it) costs the fewest bits, the
  /// third only where all of the row's reference values are below 128. With
  /// <paramref name="switchBack"/> a 0 bit and mode 0 (LZ77) follow the last row.
  /// </summary>
  internal static AceBitWriter Encode(ReadOnlySpan<byte> data, int width, int planes, bool switchBack) {
    if (width <= 0 || planes <= 0 || data.Length % width != 0)
      throw new ArgumentException("ACE PIC data must be whole rows.", nameof(data));
    var w = new AceBitWriter();
    w.WriteGolombRice(width, 12);
    w.WriteGolombRice(planes, 2);
    var plane0 = new ErrModel();
    var planeN = new ErrModel();
    var prevRow = new int[width + planes];
    for (var at = 0; at < data.Length; at += width) {
      w.WriteBits(1, 1);
      var row = new int[width + planes];
      for (var col = 0; col < width; ++col)
        row[col] = data[at + col];
      for (var plane = 0; plane < planes; ++plane) {
        if (plane == 0) {
          EncodePlane(0, plane0, plane, width, planes, prevRow, row, w);
          continue;
        }
        (int Kind, ErrModel Model, AceBitWriter Bits)? best = null;
        for (var kind = 0; kind < (ReferencesBelow128(plane, width, planes, prevRow, row) ? 3 : 2); ++kind) {
          var model = planeN.Clone();
          var bits = new AceBitWriter();
          EncodePlane(kind, model, plane, width, planes, prevRow, row, bits);
          if (best == null || bits.BitCount < best.Value.Bits.BitCount)
            best = (kind, model, bits);
        }
        w.WriteBits((uint)best!.Value.Kind, 2);
        w.Append(best.Value.Bits);
        planeN = best.Value.Model;
      }
      prevRow = row;
    }
    if (switchBack) {
      w.WriteBits(0, 1);
      w.WriteBits(AceConstants.ModeLz77, 8);
    }
    return w;
  }

  /// <summary>Whether every reference value a plane's row reads is below 128 (see <c>Scaled</c>).</summary>
  private static bool ReferencesBelow128(int plane, int width, int planes, int[] prevRow, int[] row) {
    if (Py(prevRow, plane - 1) >= 128)
      return false;
    for (var col = plane; col < width; col += planes)
      if (prevRow[col + planes - 1] >= 128 || Py(row, col - 1) >= 128)
        return false;
    return true;
  }

  private static void EncodePlane(int kind, ErrModel model, int plane, int width, int planes, int[] prevRow, int[] row, AceBitWriter w) {
    var coder = new PixelDecoder(kind);
    coder.UpdatePixelD(prevRow[plane], Py(prevRow, plane - 1));
    for (var col = plane; col < width; col += planes) {
      coder.ShiftPixels();
      coder.UpdatePixelD(prevRow[col + planes], prevRow[col + planes - 1]);
      var context = model.Contexts[coder.GetContext()];
      coder.EncodePixelX(w, context, coder.Target(row[col], Py(row, col - 1)));
    }
  }
}
