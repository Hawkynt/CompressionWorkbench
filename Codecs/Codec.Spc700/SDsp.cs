#pragma warning disable CS1591
namespace Codec.Spc700;

/// <summary>
/// The SNES S-DSP synthesizer: eight BRR voices, gaussian interpolation, ADSR/GAIN envelopes,
/// per-voice noise and pitch modulation, the 8-tap FIR echo, and the stereo main/echo mix. One
/// <see cref="Tick"/> produces a single 32&#160;kHz stereo output frame.
/// <para>The per-voice BRR decode uses exactly the math in <c>Codec.Brr.BrrCodec</c> (the
/// filters 0..3, the 16-bit clamp and 15-bit wrap) applied incrementally block-by-block, so a
/// voice decodes a BRR chain identically to that codec.</para>
/// <para>Approximations relative to silicon (documented): the 5-sample KON delay is collapsed
/// to an immediate key-on; ENVX/OUTX register read-back is best-effort; the noise generator
/// uses the documented 15-bit LFSR; gaussian interpolation is exact (verbatim table).</para>
/// </summary>
public sealed class SDsp {

  // DSP register file (128 bytes). Voice n occupies $n0-$n9; the high registers are global.
  private readonly byte[] _reg = new byte[128];
  private readonly byte[] _ram;

  /// <summary>
  /// Provides the address value.
  /// </summary>
public byte Address;

  private readonly Voice[] _voices = new Voice[8];

  // Global noise LFSR (15-bit) and its sample counter.
  private int _noise = 0x4000;
  private int _noiseCounter;

  // Echo write pointer (in ARAM, relative to ESA*0x100) and FIR delay lines.
  private int _echoOffset;
  private readonly int[] _firL = new int[8];
  private readonly int[] _firR = new int[8];

  // Global DSP register indices.
  private const int RegMVolL = 0x0C, RegMVolR = 0x1C;
  private const int RegEVolL = 0x2C, RegEVolR = 0x3C;
  private const int RegKon = 0x4C, RegKof = 0x5C, RegFlg = 0x6C, RegEndx = 0x7C;
  private const int RegEfb = 0x0D, RegPmon = 0x2D, RegNon = 0x3D, RegEon = 0x4D;
  private const int RegDir = 0x5D, RegEsa = 0x6D, RegEdl = 0x7D;
  private const int RegFir = 0x0F; // C0..C7 at $0F,$1F,...,$7F

  /// <summary>
  /// Initializes a new instance of <see cref="SDsp"/>.
  /// </summary>
public SDsp(byte[] ram) {
    this._ram = ram;
    for (var i = 0; i < 8; ++i)
      this._voices[i] = new Voice();
  }

  // ── register port ──────────────────────────────────────────────────────────────

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
public byte Read() => this._reg[this.Address & 0x7F];

  /// <summary>
  /// Writes the value to the supplied output.
  /// </summary>
public void Write(byte value) {
    var index = this.Address & 0x7F;
    this._reg[index] = value;

    // KON edge: writing KON arms the listed voices. KOF likewise releases them.
    if (index == RegKon)
      for (var v = 0; v < 8; ++v)
        if ((value & (1 << v)) != 0)
          this.KeyOn(v);
    if (index == RegKof)
      for (var v = 0; v < 8; ++v)
        if ((value & (1 << v)) != 0)
          this._voices[v].KeyOff = true;
  }

  /// <summary>Bulk-loads the 128-register file from an SPC save state's DSP block.</summary>
  public void LoadRegisters(ReadOnlySpan<byte> registers) {
    var n = Math.Min(registers.Length, this._reg.Length);
    registers[..n].CopyTo(this._reg);

    // Honor any voices already marked active in the save-state KON shadow.
    var kon = this._reg[RegKon];
    for (var v = 0; v < 8; ++v)
      if ((kon & (1 << v)) != 0)
        this.KeyOn(v);
  }

  // ── voice key-on ────────────────────────────────────────────────────────────────

  private void KeyOn(int v) {
    var voice = this._voices[v];
    var dir = this._reg[RegDir] << 8;
    var srcn = this._reg[(v << 4) | 0x04];
    var entry = dir + srcn * 4;
    if (entry + 4 > this._ram.Length)
      return;

    voice.StartAddr = this._ram[entry] | (this._ram[entry + 1] << 8);
    voice.LoopAddr = this._ram[entry + 2] | (this._ram[entry + 3] << 8);
    voice.BrrPos = voice.StartAddr;
    voice.PitchCounter = 0;
    voice.Hist1 = 0;
    voice.Hist2 = 0;
    voice.BufferFilled = 0;
    voice.Ended = false;
    voice.KeyOff = false;
    voice.Active = true;

    // Immediate attack from zero (the 5-sample hardware KON delay is collapsed; documented).
    voice.EnvLevel = 0;
    voice.EnvMode = EnvMode.Attack;

    // Clear the corresponding ENDX bit.
    this._reg[RegEndx] = (byte)(this._reg[RegEndx] & ~(1 << v));
  }

  // ── single 32 kHz tick ────────────────────────────────────────────────────────────

  /// <summary>Produces one stereo output frame; values are signed 16-bit (already clamped).</summary>
  public (short Left, short Right) Tick() {
    var flg = this._reg[RegFlg];
    var noiseEnable = this._reg[RegNon];
    var pmon = this._reg[RegPmon];
    var eon = this._reg[RegEon];

    // Advance the global noise LFSR at its FLG-selected rate.
    this.StepNoise(flg & 0x1F);

    var mainL = 0;
    var mainR = 0;
    var echoInL = 0;
    var echoInR = 0;

    var prevVoiceOutput = 0; // for pitch modulation (voice N modulated by N-1)

    for (var v = 0; v < 8; ++v) {
      var voice = this._voices[v];
      var sample = this.RunVoice(v, voice, (pmon & (1 << v)) != 0, prevVoiceOutput,
        (noiseEnable & (1 << v)) != 0, (flg & 0x80) != 0);

      prevVoiceOutput = sample; // pre-envelope/pre-volume modulator output

      var enveloped = sample * voice.EnvLevel >> 11; // ENVX is 11-bit
      var volL = (sbyte)this._reg[(v << 4) | 0x00];
      var volR = (sbyte)this._reg[(v << 4) | 0x01];

      var outL = enveloped * volL >> 7;
      var outR = enveloped * volR >> 7;

      mainL += outL;
      mainR += outR;
      if ((eon & (1 << v)) != 0) {
        echoInL += outL;
        echoInR += outR;
      }

      // ENDX / ENVX / OUTX best-effort register read-back.
      if (voice.JustEnded)
        this._reg[RegEndx] = (byte)(this._reg[RegEndx] | (1 << v));
      this._reg[(v << 4) | 0x08] = (byte)(voice.EnvLevel >> 4);          // ENVX (7-bit)
      this._reg[(v << 4) | 0x09] = (byte)((enveloped >> 7) & 0xFF);       // OUTX
    }

    // Echo: read the delay buffer, apply the FIR, feed back, and mix into the output.
    var (echoOutL, echoOutR) = this.ProcessEcho(echoInL, echoInR, flg);

    var mvolL = (sbyte)this._reg[RegMVolL];
    var mvolR = (sbyte)this._reg[RegMVolR];
    var evolL = (sbyte)this._reg[RegEVolL];
    var evolR = (sbyte)this._reg[RegEVolR];

    var finalL = (mainL * mvolL >> 7) + (echoOutL * evolL >> 7);
    var finalR = (mainR * mvolR >> 7) + (echoOutR * evolR >> 7);

    if ((flg & 0x40) != 0) { finalL = 0; finalR = 0; } // FLG mute

    return (Clamp16(finalL), Clamp16(finalR));
  }

  // ── per-voice processing ────────────────────────────────────────────────────────

  private int RunVoice(int v, Voice voice, bool pmod, int prevOutput, bool noise, bool flgReset) {
    voice.JustEnded = false;
    if (!voice.Active)
      return 0;

    if (flgReset) {
      voice.Active = false;
      voice.EnvLevel = 0;
      return 0;
    }

    // Decode enough BRR samples to prime the 4-tap gaussian window.
    while (voice.BufferFilled < 4) {
      if (!this.DecodeNextBrrSample(voice))
        break;
    }

    int output;
    if (noise) {
      output = (short)(this._noise << 1) >> 1; // 15-bit signed noise
    } else {
      // 4-point gaussian interpolation on the high 8 bits of the 12-bit pitch fraction.
      // Buffer is an oldest→newest shift register: [0]=oldest … [3]=newest.
      var frac = (voice.PitchCounter >> 4) & 0xFF;
      var b = voice.Buffer;
      var g = DspTables.Gaussian;
      var acc = (g[255 - frac] * b[0] >> 11)
              + (g[511 - frac] * b[1] >> 11)
              + (g[256 + frac] * b[2] >> 11);
      acc = (short)acc; // intermediate 15-bit wrap (documented S-DSP behaviour)
      acc += g[frac] * b[3] >> 11;
      output = Clamp16(acc);
      output = (short)(output << 1) >> 1; // final 15-bit clamp/wrap
    }

    // Advance the envelope (ADSR/GAIN), then key-off release.
    this.StepEnvelope(v, voice);

    // Pitch step (14-bit), optionally modulated by the previous voice's output.
    var pitch = (this._reg[(v << 4) | 0x02] | (this._reg[(v << 4) | 0x03] << 8)) & 0x3FFF;
    if (pmod) {
      var factor = (prevOutput >> 5) + 0x400;
      pitch = (pitch * factor) >> 10;
      if (pitch > 0x3FFF) pitch = 0x3FFF;
    }

    voice.PitchCounter += pitch;
    while (voice.PitchCounter >= 0x1000) {
      voice.PitchCounter -= 0x1000;
      // Shift one fresh decoded sample into the window (oldest drops off).
      this.DecodeNextBrrSample(voice);
    }

    return output;
  }

  /// <summary>
  /// Decodes one BRR sample into the voice's 4-entry interpolation ring, advancing through the
  /// current 9-byte block and following loop/end flags. Mirrors <c>BrrCodec</c>'s sample math.
  /// </summary>
  private bool DecodeNextBrrSample(Voice voice) {
    if (!voice.Active)
      return false;

    if (voice.NibbleIndex == 0) {
      // Fetch a fresh block header at BrrPos.
      if (voice.BrrPos + 9 > this._ram.Length)
        return false;
      voice.CurHeader = this._ram[voice.BrrPos];
    }

    var header = voice.CurHeader;
    var range = (header >> 4) & 0x0F;
    var filter = (header >> 2) & 0x03;
    var i = voice.NibbleIndex;
    var raw = this._ram[voice.BrrPos + 1 + (i >> 1)];
    var nibble = (i & 1) == 0 ? raw >> 4 : raw & 0x0F;
    var s = (nibble & 0x08) != 0 ? nibble - 16 : nibble;

    int val;
    if (range <= 12)
      val = (s << range) >> 1;
    else
      val = s >> 4;

    val += filter switch {
      1 => voice.Hist1 * 15 / 16,
      2 => voice.Hist1 * 61 / 32 - voice.Hist2 * 15 / 16,
      3 => voice.Hist1 * 115 / 64 - voice.Hist2 * 13 / 16,
      _ => 0,
    };

    val = Clamp16(val);
    var wrapped = (short)(val << 1) >> 1;
    voice.Hist2 = voice.Hist1;
    voice.Hist1 = wrapped;

    // Shift into the oldest→newest window.
    voice.Buffer[0] = voice.Buffer[1];
    voice.Buffer[1] = voice.Buffer[2];
    voice.Buffer[2] = voice.Buffer[3];
    voice.Buffer[3] = wrapped;
    if (voice.BufferFilled < 4)
      ++voice.BufferFilled;

    // Advance to the next nibble/block.
    if (++voice.NibbleIndex >= 16) {
      voice.NibbleIndex = 0;
      var ended = (header & 0x01) != 0;
      var loop = (header & 0x02) != 0;
      voice.BrrPos += 9;
      if (ended) {
        voice.JustEnded = true;
        voice.Ended = true;
        if (loop) {
          voice.BrrPos = voice.LoopAddr;
        } else {
          // End without loop → key off: ramp envelope to zero.
          voice.EnvMode = EnvMode.Release;
          voice.KeyOff = true;
        }
      }
    }

    return true;
  }

  // ── envelopes ─────────────────────────────────────────────────────────────────────

  private void StepEnvelope(int v, Voice voice) {
    var adsr1 = this._reg[(v << 4) | 0x05];
    var adsr2 = this._reg[(v << 4) | 0x06];
    var gain = this._reg[(v << 4) | 0x07];

    if (voice.KeyOff && voice.EnvMode != EnvMode.Release)
      voice.EnvMode = EnvMode.Release;

    // Release: fixed linear decrement of 8 per tick to zero (documented hardware rate).
    if (voice.EnvMode == EnvMode.Release) {
      voice.EnvLevel -= 8;
      if (voice.EnvLevel <= 0) {
        voice.EnvLevel = 0;
        voice.Active = false;
      }
      return;
    }

    var useAdsr = (adsr1 & 0x80) != 0;
    if (useAdsr) {
      this.StepAdsr(voice, adsr1, adsr2);
    } else {
      this.StepGain(voice, gain);
    }

    if (voice.EnvLevel < 0) voice.EnvLevel = 0;
    if (voice.EnvLevel > 0x7FF) voice.EnvLevel = 0x7FF;
  }

  private void StepAdsr(Voice voice, byte adsr1, byte adsr2) {
    var attackRate = ((adsr1 & 0x0F) << 1) + 1;       // AR (1..31, odd)
    var decayRate = ((adsr1 >> 4) & 0x07) * 2 + 16;   // DR (16..30 even)
    var sustainRate = adsr2 & 0x1F;                   // SR
    var sustainLevel = ((adsr2 >> 5) & 0x07);
    var sustainBound = (sustainLevel + 1) * 0x100;    // SL threshold (×0x100)

    switch (voice.EnvMode) {
      case EnvMode.Attack:
        if (this.RateReady(voice, attackRate)) {
          // Attack: linear +32 per step (or +1 from $7E0 to top, documented).
          voice.EnvLevel += attackRate == 31 ? 1024 : 32;
          if (voice.EnvLevel >= 0x7FF) {
            voice.EnvLevel = 0x7FF;
            voice.EnvMode = EnvMode.Decay;
          }
        }
        break;
      case EnvMode.Decay:
        if (this.RateReady(voice, decayRate)) {
          voice.EnvLevel -= ((voice.EnvLevel - 1) >> 8) + 1; // exponential ×255/256-style
          if (voice.EnvLevel <= sustainBound)
            voice.EnvMode = EnvMode.Sustain;
        }
        break;
      case EnvMode.Sustain:
        if (sustainRate != 0 && this.RateReady(voice, sustainRate))
          voice.EnvLevel -= ((voice.EnvLevel - 1) >> 8) + 1;
        break;
    }
  }

  private void StepGain(Voice voice, byte gain) {
    if ((gain & 0x80) == 0) {
      // Direct gain: the level is the 7-bit value scaled to 11 bits.
      voice.EnvLevel = (gain & 0x7F) << 4;
      return;
    }

    var mode = (gain >> 5) & 0x03;
    var rate = gain & 0x1F;
    if (!this.RateReady(voice, rate))
      return;

    switch (mode) {
      case 0: // linear decrease
        voice.EnvLevel -= 32;
        break;
      case 1: // exponential decrease
        voice.EnvLevel -= ((voice.EnvLevel - 1) >> 8) + 1;
        break;
      case 2: // linear increase
        voice.EnvLevel += 32;
        break;
      case 3: // bent-line increase
        voice.EnvLevel += voice.EnvLevel < 0x600 ? 32 : 8;
        break;
    }
  }

  /// <summary>True when the per-voice rate counter has reached the period for <paramref name="rate"/>.</summary>
  private bool RateReady(Voice voice, int rate) {
    if (rate == 0)
      return false;
    var period = DspTables.RatePeriod[rate & 0x1F];
    if (period == 0)
      return false;
    if (++voice.EnvCounter < period)
      return false;
    voice.EnvCounter = 0;
    return true;
  }

  // ── noise ───────────────────────────────────────────────────────────────────────

  private void StepNoise(int rate) {
    var period = DspTables.RatePeriod[rate & 0x1F];
    if (period == 0)
      return;
    if (++this._noiseCounter < period)
      return;
    this._noiseCounter = 0;
    // 15-bit Galois LFSR (taps 0 and 1), as documented for the S-DSP noise source.
    var bit = (this._noise ^ (this._noise >> 1)) & 1;
    this._noise = ((this._noise >> 1) | (bit << 14)) & 0x7FFF;
  }

  // ── echo ─────────────────────────────────────────────────────────────────────────

  private (int Left, int Right) ProcessEcho(int inL, int inR, int flg) {
    var esa = this._reg[RegEsa] << 8;
    var edl = this._reg[RegEdl] & 0x0F;
    var bufferLen = edl == 0 ? 4 : edl * 0x800; // EDL*2 KB (in 4-byte stereo samples below)

    if (this._echoOffset >= bufferLen)
      this._echoOffset = 0;

    var addr = (esa + this._echoOffset * 4) & 0xFFFF;
    if (addr + 4 > this._ram.Length)
      return (0, 0);

    // Read the delayed stereo sample from ARAM (signed 16-bit LE).
    var bufL = (short)(this._ram[addr] | (this._ram[addr + 1] << 8));
    var bufR = (short)(this._ram[addr + 2] | (this._ram[addr + 3] << 8));

    // Slide the FIR delay lines and accumulate the 8-tap response.
    for (var i = 0; i < 7; ++i) {
      this._firL[i] = this._firL[i + 1];
      this._firR[i] = this._firR[i + 1];
    }
    this._firL[7] = bufL;
    this._firR[7] = bufR;

    var outL = 0;
    var outR = 0;
    for (var i = 0; i < 8; ++i) {
      var c = (sbyte)this._reg[RegFir + (i << 4)];
      outL += this._firL[i] * c >> 7;
      outR += this._firR[i] * c >> 7;
    }
    outL = (short)Clamp16(outL);
    outR = (short)Clamp16(outR);

    // Feedback + new input written back unless echo writes are disabled (FLG bit 5).
    if ((flg & 0x20) == 0) {
      var efb = (sbyte)this._reg[RegEfb];
      var writeL = Clamp16(inL + (outL * efb >> 7));
      var writeR = Clamp16(inR + (outR * efb >> 7));
      this._ram[addr] = (byte)writeL;
      this._ram[addr + 1] = (byte)(writeL >> 8);
      this._ram[addr + 2] = (byte)writeR;
      this._ram[addr + 3] = (byte)(writeR >> 8);
    }

    ++this._echoOffset;
    return (outL, outR);
  }

  private static short Clamp16(int v) => (short)(v > 32767 ? 32767 : v < -32768 ? -32768 : v);

  // ── voice state ──────────────────────────────────────────────────────────────────

  private enum EnvMode { Attack, Decay, Sustain, Release }

  private sealed class Voice {
    public bool Active;
    public bool KeyOff;
    public bool Ended;
    public bool JustEnded;

    public int StartAddr;
    public int LoopAddr;
    public int BrrPos;
    public int NibbleIndex;
    public byte CurHeader;

    public int Hist1;
    public int Hist2;

    // 4-tap interpolation window as an oldest→newest shift register.
    public readonly int[] Buffer = new int[4];
    public int BufferFilled;

    public int PitchCounter; // 12-bit fractional position

    public int EnvLevel;     // 0..0x7FF
    public EnvMode EnvMode;
    public int EnvCounter;
  }
}
