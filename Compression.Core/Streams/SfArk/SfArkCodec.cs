using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Compression.Core.Deflate;
using Compression.Registry;
using FileFormat.Zlib;

namespace Compression.Core.Streams.SfArk;

/// <summary>What an sfArk header and its text files say about the packed SoundFont.</summary>
/// <param name="FileName">Original file name.</param>
/// <param name="Method">Compression method: 4 Turbo, 5 Fast, 6 Standard, 7 Max.</param>
/// <param name="CreatorVersion">Version string of the program that wrote the file.</param>
/// <param name="Notes">The notes text, when present.</param>
/// <param name="License">The licence text, when present.</param>
public sealed record SfArkInfo(string FileName, int Method, string CreatorVersion, byte[]? Notes, byte[]? License);

/// <summary>
/// sfArk V2 (<c>.sfArk</c>): SoundFont 2 banks with the sample data coded losslessly by
/// prediction (integrators, optional LPC lattice) and Rice-style residual codes, the rest zlib.
/// </summary>
/// <remarks>
/// Implemented from <c>docs/SFARK-ON-DISK.md</c>, which was written from the GPL-3.0 sfArkLib
/// decoder (sourcing rung 2); no code was carried across. Verified against the sfarkxtc
/// reference decoder on published files and on files this class writes. The writer uses the
/// Fast method only; V1 files (methods 0-2) are refused, as the reference refuses them.
/// </remarks>
public static class SfArkCodec {

  /// <summary>The signature, at <see cref="SignatureOffset"/> of the header.</summary>
  public static ReadOnlySpan<byte> Signature => "sfArk"u8;

  /// <summary>Offset of <see cref="Signature"/> in the header.</summary>
  public const int SignatureOffset = 26;

  private const int FixedHeaderSize = 42;
  private const int MaxNameLength = 255;
  private const int MaxHeaderSearch = 128 * 1024;
  private const int MaxNonAudioBlock = 256 * 1024;
  private const int MethodTurbo = 4, MethodFast = 5, MethodStandard = 6, MethodMax = 7;
  private const int MaxBlock = 4096;
  private const int SlotSize = 64;
  private const int LpcWindow = 128;
  private const int MaxLpcOrder = 128;
  private const int HistorySlots = 4;
  private const int LpcScaleBits = 14;
  private const int ResidualWindow = 32;
  private const int TurboResidualWindow = 256;
  private const int EncoderBlock = 1024;
  private const int EncoderChunk = 64 * 1024;

  // ── checks ───────────────────────────────────────────────────────────

  /// <summary>Adler-32 update; sfArk seeds it with 0 rather than 1.</summary>
  private static uint Adler(uint seed, ReadOnlySpan<byte> data) {
    const uint Base = 65521;
    const int Chunk = 5552;
    uint a = seed & 0xFFFF, b = seed >> 16;
    a %= Base;
    b %= Base;
    while (!data.IsEmpty) {
      var n = Math.Min(Chunk, data.Length);
      foreach (var x in data[..n]) {
        a += x;
        b += a;
      }
      a %= Base;
      b %= Base;
      data = data[n..];
    }
    return (b << 16) | a;
  }

  /// <summary>File-check update for an audio block: <c>2·check + Σ (x XOR x&gt;&gt;15)</c>.</summary>
  private static uint AudioCheck(uint check, ReadOnlySpan<short> samples) {
    long total = 0;
    foreach (var x in samples) total += x ^ (x >> 15);
    return unchecked((uint)(2L * check + total));
  }

  // ── bit I/O ──────────────────────────────────────────────────────────

  /// <summary>16-bit little-endian words, each read from its most significant bit down.</summary>
  private ref struct BitReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;
    private ulong _acc;
    private int _count;

    private void Refill() {
      while (this._count <= 32 && this._pos < this._data.Length) {
        int word = this._data[this._pos];
        if (this._pos + 1 < this._data.Length) word |= this._data[this._pos + 1] << 8;
        this._pos += 2;
        this._acc = (this._acc << 16) | (uint)word;
        this._count += 16;
      }
    }

    public int Bits(int n) {
      if (n == 0) return 0;
      if (this._count < n) {
        this.Refill();
        if (this._count < n) throw new InvalidDataException("sfArk: unexpected end of data.");
      }
      this._count -= n;
      return (int)((this._acc >> this._count) & ((1UL << n) - 1));
    }

    public bool Flag() => this.Bits(1) != 0;

    public int Unary() {
      var zeros = 0;
      for (;;) {
        if (this._count == 0) {
          this.Refill();
          if (this._count == 0) throw new InvalidDataException("sfArk: unexpected end of data.");
        }
        var live = this._acc & ((1UL << this._count) - 1);
        if (live == 0) {
          zeros += this._count;
          this._count = 0;
          continue;
        }
        var lead = BitOperations.LeadingZeroCount(live) - (64 - this._count);
        zeros += lead;
        this._count -= lead + 1;
        return zeros;
      }
    }

    public short Delta(int previous) {
      var m = this.Unary();
      if (m != 0 && this.Flag()) m = -m;
      return (short)(previous + m);
    }

    public byte Byte() => (byte)this.Bits(8);
  }

  private sealed class BitWriter {
    private readonly MemoryStream _bytes = new();
    private int _acc, _count;

    public void Bits(int value, int n) {
      for (var i = n - 1; i >= 0; --i) this.Bit((value >> i) & 1);
    }

    public void Bit(int b) {
      this._acc = (this._acc << 1) | (b & 1);
      if (++this._count == 16) {
        this._bytes.WriteByte((byte)this._acc);
        this._bytes.WriteByte((byte)(this._acc >> 8));
        this._acc = this._count = 0;
      }
    }

    public void Unary(int zeros) {
      for (var i = 0; i < zeros; ++i) this.Bit(0);
      this.Bit(1);
    }

    public void Delta(int previous, int value) {
      var d = value - previous;
      this.Unary(Math.Abs(d));
      if (d != 0) this.Bit(d < 0 ? 1 : 0);
    }

    public void Byte(byte b) => this.Bits(b, 8);

    public byte[] Finish() {
      while (this._count != 0) this.Bit(0);
      return this._bytes.ToArray();
    }
  }

  // ── header ───────────────────────────────────────────────────────────

  private readonly record struct Header(
    int Offset, int Length, uint Flags, uint OriginalSize, uint FileCheck, int Method,
    string Version, uint AudioStart, uint PostAudioStart, string Name);

  private static Header ReadHeader(ReadOnlySpan<byte> data) {
    bool signatureSeen = false, v1Seen = false;
    for (var attempt = 0; attempt < MaxHeaderSearch; ++attempt) {
      var at = attempt == 0 ? 0 : MaxHeaderSearch - attempt;
      if (at + FixedHeaderSize + 1 > data.Length) continue;
      if (!data.Slice(at + SignatureOffset, Signature.Length).SequenceEqual(Signature)) continue;
      signatureSeen = true;
      var method = data[at + 31];
      if (method < 3) {
        v1Seen = true;
        continue;
      }
      var nameField = data.Slice(at + FixedHeaderSize, Math.Min(MaxNameLength, data.Length - at - FixedHeaderSize));
      var nameLength = nameField.IndexOf((byte)0);
      if (nameLength < 0) nameLength = nameField.Length;
      var length = FixedHeaderSize + nameLength + 1;
      if (at + length > data.Length) continue;
      var h = data[at..];
      var copy = h[..length].ToArray();
      copy.AsSpan(16, 4).Clear();
      if (Adler(0, copy) != BinaryPrimitives.ReadUInt32LittleEndian(h[16..])) continue;
      return new(at, length,
        BinaryPrimitives.ReadUInt32LittleEndian(h),
        BinaryPrimitives.ReadUInt32LittleEndian(h[4..]),
        BinaryPrimitives.ReadUInt32LittleEndian(h[12..]),
        method,
        Encoding.ASCII.GetString(h.Slice(21, 5)),
        BinaryPrimitives.ReadUInt32LittleEndian(h[34..]),
        BinaryPrimitives.ReadUInt32LittleEndian(h[38..]),
        Encoding.Latin1.GetString(nameField[..nameLength]));
    }
    if (v1Seen) throw new NotSupportedException("sfArk: version 1 files are not supported.");
    throw new InvalidDataException(signatureSeen ? "sfArk: header check failed." : "sfArk: no sfArk header found.");
  }

  // ── decoder state ────────────────────────────────────────────────────

  /// <summary>Lattice and autocorrelation history of the LPC stage, kept across blocks.</summary>
  private sealed class LpcState {
    public readonly int[] Lattice = new int[MaxLpcOrder + 1];
    public readonly float[][] History = [new float[MaxLpcOrder + 1], new float[MaxLpcOrder + 1], new float[MaxLpcOrder + 1], new float[MaxLpcOrder + 1]];
    public readonly int[] PreviousStart = new int[MaxLpcOrder];
    public int Slot;

    public void Reset() {
      Array.Clear(this.Lattice);
      foreach (var h in this.History) Array.Clear(h);
      Array.Clear(this.PreviousStart);
      this.Slot = 0;
    }
  }

  private sealed class DecoderState(int method) {
    public readonly int Method = method;
    public readonly int BlockSize = method == MethodFast ? 1024 : 4096;
    public readonly int MaxLoops = method == MethodFast ? 20 : 3;
    public readonly int MaxAverageLoops = method switch { MethodFast => 20, MethodMax => 5, MethodStandard => 3, _ => 0 };
    public readonly int LpcOrder = method switch { MethodStandard => 8, MethodMax => 128, _ => 0 };
    public readonly short[] History = new short[20];
    public readonly LpcState Lpc = new();
    public short Fix = 8, Loops, AverageLoops, Shift, LastShift;
    public uint Check;
    public short[] Work = new short[MaxBlock];
    public short[] Spare = new short[MaxBlock];

    public void Swap() => (this.Work, this.Spare) = (this.Spare, this.Work);
  }

  private enum Section { PreAudio, Audio, PostAudio, Finished }

  // ── decoder ──────────────────────────────────────────────────────────

  /// <summary>Decodes an sfArk V2 file to the SoundFont it packs.</summary>
  /// <exception cref="InvalidDataException">Not an sfArk file, or corrupt.</exception>
  /// <exception cref="NotSupportedException">sfArk V1, or a method no decoder knows.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> data) => Decode(data, out _);

  /// <summary>Decodes an sfArk V2 file and reports what its header and text files say.</summary>
  /// <exception cref="InvalidDataException">Not an sfArk file, or corrupt.</exception>
  /// <exception cref="NotSupportedException">sfArk V1, or a method no decoder knows.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> data, out SfArkInfo info) {
    var header = ReadHeader(data);
    if (header.Method is not (MethodTurbo or MethodFast or MethodStandard or MethodMax))
      throw new NotSupportedException($"sfArk: compression method {header.Method} is not supported.");
    if (header.PostAudioStart < header.AudioStart)
      throw new InvalidDataException("sfArk: audio ends before it starts.");

    var state = new DecoderState(header.Method);
    var pos = header.Offset + header.Length;
    byte[]? license = null, notes = null;
    if ((header.Flags & 2) != 0) license = ReadTextFile(data, ref pos, ref state.Check);
    if ((header.Flags & 1) != 0) notes = ReadTextFile(data, ref pos, ref state.Check);

    var reader = new BitReader(data[pos..]);
    using var output = new MemoryStream((int)Math.Min(header.OriginalSize, 1u << 28));
    long total = 0;
    var section = Section.PreAudio;
    var blockBytes = new byte[2 * MaxBlock];
    var packed = new byte[MaxNonAudioBlock];
    while (section != Section.Finished) {
      if (section == Section.Audio) {
        long n = 2L * state.BlockSize;
        if (total + n >= header.PostAudioStart) {
          n = header.PostAudioStart - total;
          section = Section.PostAudio;
        }
        if ((n & 1) != 0) throw new InvalidDataException("sfArk: audio section has an odd length.");
        var words = (int)(n / 2);
        DecodeAudioBlock(ref reader, state, words);
        for (var i = 0; i < words; ++i) BinaryPrimitives.WriteInt16LittleEndian(blockBytes.AsSpan(2 * i), state.Work[i]);
        output.Write(blockBytes, 0, (int)n);
        total += n;
        continue;
      }

      uint length = 0;
      for (var i = 0; i < 4; ++i) length |= (uint)reader.Byte() << (8 * i);
      if (length > MaxNonAudioBlock) throw new InvalidDataException("sfArk: bad non-audio block length.");
      for (var i = 0; i < length; ++i) packed[i] = reader.Byte();
      var plain = Inflate(packed.AsSpan(0, (int)length));
      state.Check = Adler(state.Check, plain);
      output.Write(plain);
      total += plain.Length;
      if (total >= header.OriginalSize) section = Section.Finished;
      else if (section == Section.PreAudio && total >= header.AudioStart) section = Section.Audio;
    }

    if (state.Check != header.FileCheck) throw new InvalidDataException("sfArk: file check failed.");
    info = new(header.Name, header.Method, header.Version, notes, license);
    return output.ToArray();
  }

  private static byte[] ReadTextFile(ReadOnlySpan<byte> data, ref int pos, ref uint check) {
    if (pos + 4 > data.Length) throw new InvalidDataException("sfArk: truncated text file.");
    var length = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
    pos += 4;
    if (length is 0 or > MaxNonAudioBlock || length > data.Length - pos) throw new InvalidDataException("sfArk: bad text file length.");
    var plain = Inflate(data.Slice(pos, (int)length));
    pos += (int)length;
    check = Adler(check, plain);
    return plain;
  }

  private static byte[] Inflate(ReadOnlySpan<byte> zlib) {
    byte[] plain;
    try {
      plain = ZlibStream.Decompress(zlib);
    } catch (Exception e) when (e is not InvalidDataException) {
      throw new InvalidDataException("sfArk: bad zlib block.", e);
    }
    if (plain.Length > MaxNonAudioBlock) throw new InvalidDataException("sfArk: non-audio block too large.");
    return plain;
  }

  private static void DecodeAudioBlock(ref BitReader reader, DecoderState state, int n) {
    if (state.Method == MethodTurbo) {
      var loops = reader.Delta(state.Loops);
      if (loops < 0 || loops > state.MaxLoops) throw new InvalidDataException("sfArk: bad loop count.");
      state.Loops = loops;
      ReadResiduals(ref reader, state, state.Work.AsSpan(0, n), TurboResidualWindow);
      for (var j = loops - 1; j >= 0; --j) {
        if (j == 0) state.Check = AudioCheck(state.Check, state.Work.AsSpan(0, n));
        Integrate(state.Work, state.Spare, n, ref state.History[j]);
        state.Swap();
      }
      return;
    }

    Span<short> shifts = stackalloc short[MaxBlock / SlotSize];
    var shifted = ReadShiftMap(ref reader, state, n, shifts);
    var average = reader.Flag();
    Span<bool> smooth = stackalloc bool[20];
    int count;
    if (average) {
      count = reader.Delta(state.AverageLoops);
      if (count < 0 || count > state.MaxAverageLoops) throw new InvalidDataException("sfArk: bad loop count.");
      state.AverageLoops = (short)count;
    } else {
      count = reader.Delta(state.Loops);
      if (count < 0 || count > state.MaxLoops) throw new InvalidDataException("sfArk: bad loop count.");
      state.Loops = (short)count;
      for (var i = 0; i < count; ++i) smooth[i] = reader.Flag();
    }
    uint resetMask = 0;
    if (state.LpcOrder != 0 && reader.Flag()) {
      var low = (uint)reader.Bits(16);
      resetMask = low | ((uint)reader.Bits(16) << 16);
    }

    ReadResiduals(ref reader, state, state.Work.AsSpan(0, n), ResidualWindow);
    if (state.LpcOrder != 0) {
      Synthesize(state.Lpc, state.Work, state.Spare, n, state.LpcOrder, resetMask);
      state.Swap();
    }
    for (var j = count - 1; j >= 0; --j) {
      if (average) AverageIntegrate(state.Work, state.Spare, n, ref state.History[j]);
      else if (smooth[j]) SmoothIntegrate(state.Work, state.Spare, n, ref state.History[j]);
      else Integrate(state.Work, state.Spare, n, ref state.History[j]);
      state.Swap();
    }
    if (shifted)
      for (var slot = 0; slot * SlotSize < n; ++slot) {
        var s = shifts[slot];
        if (s == 0) continue;
        var end = Math.Min(n, (slot + 1) * SlotSize);
        for (var i = slot * SlotSize; i < end; ++i) state.Work[i] = (short)(state.Work[i] << (s & 31));
      }
    state.Check = AudioCheck(state.Check, state.Work.AsSpan(0, n));
  }

  private static bool ReadShiftMap(ref BitReader reader, DecoderState state, int n, scoped Span<short> shifts) {
    if (!reader.Flag()) return false;
    var slots = (n + SlotSize - 1) / SlotSize;
    int position = 0, filled = 0;
    while (reader.Flag()) {
      var room = slots - position - 1;
      if (room < 0) throw new InvalidDataException("sfArk: bad shift map.");
      position += reader.Bits(32 - BitOperations.LeadingZeroCount((uint)room));
      short next;
      if (state.Shift == 0) {
        next = reader.Delta(state.LastShift);
        state.LastShift = next;
      } else
        next = reader.Delta(0);
      if (position > slots) throw new InvalidDataException("sfArk: bad shift map.");
      for (; filled < position; ++filled) shifts[filled] = state.Shift;
      state.Shift = next;
    }
    for (; filled < slots; ++filled) shifts[filled] = state.Shift;
    return true;
  }

  private static void ReadResiduals(ref BitReader reader, DecoderState state, Span<short> buffer, int window) {
    for (var start = 0; start < buffer.Length; start += window) {
      var part = buffer.Slice(start, Math.Min(window, buffer.Length - start));
      var fix = state.Fix = reader.Delta(state.Fix);
      switch (fix) {
        case >= 0 and < 14:
          for (var i = 0; i < part.Length; ++i) {
            var lo = reader.Bits(fix + 1);
            var g = reader.Unary();
            part[i] = (short)(((g << fix) | (lo >> 1)) ^ -(lo & 1));
          }
          break;
        case 14:
          for (var i = 0; i < part.Length; ++i) part[i] = (short)reader.Bits(16);
          break;
        case -1:
          for (var i = 0; i < part.Length; ++i) part[i] = (short)-reader.Bits(1);
          break;
        case -2:
          part.Clear();
          break;
        default:
          throw new InvalidDataException("sfArk: bad residual window.");
      }
    }
  }

  private static void Integrate(short[] input, short[] output, int n, ref short history) {
    if (n == 0) return;
    var last = history;
    for (var i = 0; i < n; ++i) last = output[i] = (short)(input[i] + last);
    history = last;
  }

  private static void SmoothIntegrate(short[] input, short[] output, int n, ref short history) {
    if (n == 0) return;
    output[n - 1] = input[n - 1];
    if (n > 1) {
      for (var i = n - 2; i >= 1; --i) output[i] = (short)(input[i] + ((input[i - 1] + output[i + 1]) >> 1));
      output[0] = (short)(input[0] + (output[1] >> 1));
    }
    history = output[n - 1];
  }

  private static void AverageIntegrate(short[] input, short[] output, int n, ref short history) {
    var average = history;
    for (var i = 0; i < n; ++i) {
      output[i] = (short)(input[i] + average);
      average = (short)(average + input[i] / 2);
    }
    history = average;
  }

  // ── LPC ──────────────────────────────────────────────────────────────

  private static long ScaleDown(long x) => x >= 0 ? x >> LpcScaleBits : -(-x >> LpcScaleBits);

  /// <summary>Float to integer as x86 truncation does it: NaN and overflow give the minimum value.</summary>
  private static long Truncate(float x) => float.IsNaN(x) || x >= 9.223372E18f || x < -9.223372E18f ? long.MinValue : (long)x;

  private static void Synthesize(LpcState lpc, short[] input, short[] output, int n, int order, uint resetMask) {
    if (n < LpcWindow) {
      Array.Copy(input, output, n);
      return;
    }
    var padded = (n + LpcWindow - 1) / LpcWindow * LpcWindow;
    var residual = new int[padded];
    var signal = new int[padded];
    for (var i = 0; i < n; ++i) residual[i] = input[i];
    var correlation = new float[order + 1];
    var coefficients = new long[order];
    var bit = 1u;
    for (var start = 0; start < n; start += LpcWindow, bit <<= 1) {
      for (var k = 0; k <= order; ++k)
        correlation[k] = (float)((double)lpc.History[0][k] + lpc.History[1][k] + lpc.History[2][k] + lpc.History[3][k]);
      var window = signal.AsSpan(start, LpcWindow);
      if ((resetMask & bit) == 0) {
        Schur(correlation, order, coefficients);
        Lattice(lpc.Lattice, coefficients, order, residual.AsSpan(start, LpcWindow), window);
      } else {
        lpc.Reset();
        residual.AsSpan(start, LpcWindow).CopyTo(window);
      }
      AddCrossCorrelation(lpc.PreviousStart, window, order, lpc.History[lpc.Slot]);
      lpc.Slot = (lpc.Slot + 1) % HistorySlots;
      AutoCorrelation(window, order, lpc.History[lpc.Slot]);
      window[..order].CopyTo(lpc.PreviousStart);
    }
    for (var i = 0; i < n; ++i) output[i] = (short)signal[i];
  }

  private static void Schur(float[] r, int order, long[] coefficients) {
    if (r[0] == 0) {
      Array.Clear(coefficients);
      return;
    }
    Span<float> g0 = stackalloc float[MaxLpcOrder];
    Span<float> g1 = stackalloc float[MaxLpcOrder];
    for (var i = 0; i < order; ++i) g0[i] = g1[i] = r[i + 1];
    var error = r[0];
    for (var i = 0; ;) {
      var k = -(float)((double)g1[0] / error);
      error = (float)(error + (double)g1[0] * k);
      coefficients[i] = Truncate((float)((double)k * (1 << LpcScaleBits)));
      if (++i >= order) break;
      for (var m = 0; m < order - i; ++m) {
        g1[m] = (float)(g1[m + 1] + (double)k * g0[m]);
        g0[m] = (float)(g0[m] + (double)k * g1[m + 1]);
      }
    }
  }

  private static void Lattice(int[] u, long[] c, int order, ReadOnlySpan<int> residual, Span<int> signal) {
    unchecked {
      for (var j = 0; j < residual.Length; ++j) {
        var s = residual[j];
        for (var i = order - 1; i >= 0; --i) {
          s = (int)(s - ScaleDown(c[i] * u[i]));
          u[i + 1] = (int)(u[i] + ScaleDown(c[i] * s));
        }
        signal[j] = u[0] = s;
      }
    }
  }

  /// <summary>Σ b[i]·b[i+lag] for i in [from, to): double terms, rounded to float after every 16, then term by term.</summary>
  private static float GroupedSum(ReadOnlySpan<float> b, int from, int to, int lag) {
    var c = 0f;
    var i = from;
    for (; i < to - 15; i += 16) {
      var d = (double)c;
      for (var t = 0; t < 16; ++t) d += (double)b[i + t] * b[i + t + lag];
      c = (float)d;
    }
    for (; i < to; ++i) c = (float)(c + (double)b[i] * b[i + lag]);
    return c;
  }

  private static void AutoCorrelation(ReadOnlySpan<int> window, int order, float[] slot) {
    Span<float> b = stackalloc float[LpcWindow];
    for (var i = 0; i < LpcWindow; ++i) b[i] = window[i];
    for (var lag = order; lag >= 0; --lag) slot[lag] = GroupedSum(b, 0, LpcWindow - lag, lag);
  }

  private static void AddCrossCorrelation(int[] previous, ReadOnlySpan<int> window, int order, float[] slot) {
    Span<float> b = stackalloc float[2 * MaxLpcOrder];
    for (var i = 0; i < order; ++i) {
      b[i] = previous[i];
      b[i + order] = window[i];
    }
    for (var lag = order; lag >= 1; --lag) slot[lag] = (float)(slot[lag] + (double)GroupedSum(b, order - lag, order, lag));
  }

  // ── encoder ──────────────────────────────────────────────────────────

  /// <summary>
  /// Packs a file as sfArk (Fast method). For a SoundFont 2 bank the <c>smpl</c> data is coded
  /// as audio; any other input is stored as zlib blocks only.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<byte> data, string fileName = "sound.sf2") {
    var (audioStart, audioEnd) = FindSampleData(data);
    var w = new BitWriter();
    uint check = 0;

    void NonAudio(ReadOnlySpan<byte> region) {
      // At least one block, even an empty one: the decoder always expects one here.
      do {
        var part = region[..Math.Min(EncoderChunk, region.Length)];
        var packed = ZlibStream.Compress(part, DeflateCompressionLevel.Default);
        for (var i = 0; i < 4; ++i) w.Byte((byte)(packed.Length >> (8 * i)));
        foreach (var b in packed) w.Byte(b);
        check = Adler(check, part);
        region = region[part.Length..];
      } while (!region.IsEmpty);
    }

    NonAudio(data[..audioStart]);
    if (audioEnd > audioStart) {
      short history = 0, fix = 8;
      var loops = 0;
      var samples = new short[EncoderBlock];
      var residuals = new short[EncoderBlock];
      for (var at = audioStart; at < audioEnd; at += 2 * EncoderBlock) {
        var n = Math.Min(EncoderBlock, (audioEnd - at) / 2);
        for (var i = 0; i < n; ++i) {
          samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(at + 2 * i)..]);
          residuals[i] = (short)(samples[i] - (i == 0 ? history : samples[i - 1]));
        }
        history = samples[n - 1];
        w.Bit(0);           // no shift map
        w.Bit(0);           // plain integrators
        w.Delta(loops, 1);  // one of them
        loops = 1;
        w.Bit(0);           // first-order
        for (var s = 0; s < n; s += ResidualWindow)
          fix = WriteResidualWindow(w, residuals.AsSpan(s, Math.Min(ResidualWindow, n - s)), fix);
        check = AudioCheck(check, samples.AsSpan(0, n));
      }
      NonAudio(data[audioEnd..]);
    }
    var body = w.Finish();

    var name = Encoding.Latin1.GetBytes(fileName.Length > MaxNameLength ? fileName[..MaxNameLength] : fileName);
    var header = new byte[FixedHeaderSize + name.Length + 1];
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)data.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)(header.Length + body.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), check);
    header[20] = 20;
    "2.15 "u8.CopyTo(header.AsSpan(21));
    Signature.CopyTo(header.AsSpan(SignatureOffset));
    header[31] = MethodFast;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(34), (uint)audioStart);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(38), (uint)audioEnd);
    name.CopyTo(header.AsSpan(FixedHeaderSize));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), Adler(0, header));
    return [.. header, .. body];
  }

  private static short WriteResidualWindow(BitWriter w, ReadOnlySpan<short> values, short previousFix) {
    bool allZero = true, zeroOrMinusOne = true;
    foreach (var v in values) {
      if (v != 0) allZero = false;
      if (v is not (0 or -1)) zeroOrMinusOne = false;
    }
    short fix;
    if (allZero) fix = -2;
    else if (zeroOrMinusOne) fix = -1;
    else {
      fix = 14;
      var best = 16L * values.Length;
      for (short f = 0; f < 14; ++f) {
        var cost = (long)(f + 2) * values.Length;
        foreach (var v in values) cost += (v < 0 ? ~v : v) >> f;
        if (cost < best) {
          best = cost;
          fix = f;
        }
      }
    }
    w.Delta(previousFix, fix);
    foreach (var v in values)
      switch (fix) {
        case -1:
          w.Bit(v == -1 ? 1 : 0);
          break;
        case 14:
          w.Bits((ushort)v, 16);
          break;
        case >= 0: {
          var sign = v < 0 ? 1 : 0;
          var m = v < 0 ? ~v : v;
          w.Bits(((m & ((1 << fix) - 1)) << 1) | sign, fix + 1);
          w.Unary(m >> fix);
          break;
        }
      }
    return fix;
  }

  /// <summary>Offsets of the <c>smpl</c> chunk data in a RIFF <c>sfbk</c> file; (length, length) otherwise.</summary>
  private static (int Start, int End) FindSampleData(ReadOnlySpan<byte> data) {
    var none = (data.Length, data.Length);
    if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("sfbk"u8)) return none;
    long pos = 12;
    while (pos + 8 <= data.Length) {
      var p = (int)pos;
      var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 4)..]);
      if (data.Slice(p, 4).SequenceEqual("LIST"u8) && p + 12 <= data.Length && data.Slice(p + 8, 4).SequenceEqual("sdta"u8)) {
        var end = Math.Min(data.Length, pos + 8 + size);
        long inner = p + 12;
        while (inner + 8 <= end) {
          var q = (int)inner;
          var chunk = BinaryPrimitives.ReadUInt32LittleEndian(data[(q + 4)..]);
          if (data.Slice(q, 4).SequenceEqual("smpl"u8)) {
            var start = q + 8;
            var length = (int)Math.Min(chunk, end - start) & ~1;
            return length > 0 ? (start, start + length) : none;
          }
          inner += 8 + chunk + (chunk & 1);
        }
        return none;
      }
      pos += 8 + size + (size & 1);
    }
    return none;
  }
}

/// <summary>Building block for sfArk V2; writes the Fast method.</summary>
public sealed class SfArkBuildingBlock : IBuildingBlock {
  /// <inheritdoc />
  public string Id => "BB_SfArk";
  /// <inheritdoc />
  public string DisplayName => "sfArk";
  /// <inheritdoc />
  public string Description => "sfArk V2 SoundFont coding: integrators, LPC lattice and Rice-style residuals, zlib for the rest";
  /// <inheritdoc />
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  /// <inheritdoc />
  public byte[] Compress(ReadOnlySpan<byte> data) => SfArkCodec.Encode(data);
  /// <inheritdoc />
  public byte[] Decompress(ReadOnlySpan<byte> data) => SfArkCodec.Decode(data);
}
