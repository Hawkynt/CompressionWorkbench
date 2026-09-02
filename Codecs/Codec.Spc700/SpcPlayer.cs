#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.Spc700;

/// <summary>
/// Renders an SPC700 save state (<c>.spc</c>) to stereo 32&#160;kHz PCM by booting the
/// <see cref="Spc700Cpu"/> from the snapshot's register/ARAM/DSP state and stepping it in lock
/// with the <see cref="SDsp"/>: 32 CPU master cycles per 32&#160;kHz output frame
/// (1.024&#160;MHz / 32&#160;kHz), with the hardware timers advancing by the cycles actually
/// consumed each step.
/// <para>The duration follows the ID666 song-length tag (text format, three ASCII digits at
/// offset <c>0xA9</c>) capped to 300&#160;s; absent or unparsable, a 30&#160;s default is
/// rendered.</para>
/// </summary>
public sealed class SpcPlayer {

  /// <summary>
  /// Defines the sample rate constant value.
  /// </summary>
public const int SampleRate = 32000;
  private const int CpuCyclesPerSample = 32; // 1_024_000 / 32_000
  private const int DefaultSeconds = 30;
  private const int MaxSeconds = 300;

  private const int AramOffset = 0x100;
  private const int AramSize = 0x10000;
  private const int DspOffset = 0x10100;
  private const int DspSize = 128;
  private const int RegPcOffset = 0x25;   // u16 LE
  private const int RegAOffset = 0x27;
  private const int RegXOffset = 0x28;
  private const int RegYOffset = 0x29;
  private const int RegPswOffset = 0x2A;
  private const int RegSpOffset = 0x2B;
  private const int SongLengthOffset = 0xA9; // ID666 text: 3 ASCII digits (seconds)

  private readonly Apu _apu;
  private readonly Spc700Cpu _cpu;

  /// <summary>Rendered duration in seconds (resolved from the ID666 tag or the default).</summary>
  public int DurationSeconds { get; }

  /// <summary>True when the duration came from the file's ID666 song-length tag.</summary>
  public bool DurationFromTag { get; }

  /// <summary>
  /// Builds a player from a complete SPC blob (must be at least <c>0x10180</c> bytes). Loads the
  /// ARAM, the CPU registers and the DSP register file, and seeds the timer targets.
  /// </summary>
  public SpcPlayer(ReadOnlySpan<byte> spc) {
    if (spc.Length < DspOffset + DspSize)
      throw new ArgumentException("SPC blob too short to contain ARAM and DSP state.", nameof(spc));

    this._apu = new Apu();
    spc.Slice(AramOffset, AramSize).CopyTo(this._apu.Ram);
    this._apu.Dsp.LoadRegisters(spc.Slice(DspOffset, DspSize));
    this._apu.InitializeFromRam();

    this._cpu = new Spc700Cpu(this._apu) {
      Pc = BinaryPrimitives.ReadUInt16LittleEndian(spc.Slice(RegPcOffset, 2)),
      A = spc[RegAOffset],
      X = spc[RegXOffset],
      Y = spc[RegYOffset],
      Psw = spc[RegPswOffset],
      Sp = spc[RegSpOffset],
    };

    var (seconds, fromTag) = ResolveDuration(spc);
    this.DurationSeconds = seconds;
    this.DurationFromTag = fromTag;
  }

  private static (int Seconds, bool FromTag) ResolveDuration(ReadOnlySpan<byte> spc) {
    if (spc.Length >= SongLengthOffset + 3) {
      var digits = spc.Slice(SongLengthOffset, 3);
      var value = 0;
      var any = false;
      var valid = true;
      foreach (var b in digits) {
        if (b is >= (byte)'0' and <= (byte)'9') {
          value = value * 10 + (b - '0');
          any = true;
        } else if (b is 0 or (byte)' ') {
          // trailing padding is allowed
        } else {
          valid = false;
          break;
        }
      }
      if (valid && any && value > 0)
        return (Math.Min(value, MaxSeconds), true);
    }
    return (DefaultSeconds, false);
  }

  /// <summary>
  /// Renders the full tune to interleaved signed 16-bit stereo PCM (L,R,L,R…) at 32&#160;kHz.
  /// </summary>
  public byte[] RenderInterleavedPcm() {
    var frames = this.DurationSeconds * SampleRate;
    var pcm = new byte[frames * 4];

    var cycleDebt = 0;
    for (var f = 0; f < frames; ++f) {
      // Run ~32 CPU master cycles, accounting for instruction over-run via a debt carry.
      cycleDebt += CpuCyclesPerSample;
      while (cycleDebt > 0) {
        var cycles = this._cpu.Step();
        this._apu.StepTimers(cycles);
        cycleDebt -= cycles;
      }

      var (l, r) = this._apu.Dsp.Tick();
      var off = f * 4;
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(off), l);
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(off + 2), r);
    }

    return pcm;
  }

  /// <summary>
  /// Renders the tune and splits it into the two mono channels as signed 16-bit LE PCM,
  /// ready to be wrapped as <c>LEFT.wav</c> / <c>RIGHT.wav</c>.
  /// </summary>
  public (byte[] Left, byte[] Right) RenderStereoChannels() {
    var interleaved = this.RenderInterleavedPcm();
    var frames = interleaved.Length / 4;
    var left = new byte[frames * 2];
    var right = new byte[frames * 2];
    for (var f = 0; f < frames; ++f) {
      left[f * 2] = interleaved[f * 4];
      left[f * 2 + 1] = interleaved[f * 4 + 1];
      right[f * 2] = interleaved[f * 4 + 2];
      right[f * 2 + 1] = interleaved[f * 4 + 3];
    }
    return (left, right);
  }
}
