#pragma warning disable CS1591
namespace Codec.Opl;

/// <summary>
/// One OPL FM operator: a phase generator (F-number × block × MUL with vibrato), an ADSR
/// envelope generator (with KSR rate-scaling and KSL level-scaling), and a waveform-shaped
/// log-sine output. The operator logic follows <c>Nuked-OPL3</c>'s <c>opl3_slot</c>:
/// <c>output = exp(logsin(phase + modulation) + envelope + ksl + tl + tremolo)</c>.
/// <para>Eight waveforms are supported. Waveforms 0..3 (sine, half-sine, abs-sine, quarter/
/// pulse-sine) exist on OPL2 (register 0xE0); waveforms 4..7 (alternating sine, camel sine,
/// square, log-sawtooth) are the OPL3-only additions enabled by register 0x01 bit5 / 0x105.</para>
/// </summary>
internal sealed class OplOperator {

  // ── envelope phases ──
  internal enum EgState { Release, Attack, Decay, Sustain }

  // Maximum attenuation in 1/256-dB log domain steps (0x1FF = ~511, silence) — Nuked uses 0x1FF.
  internal const int MaxAttenuation = 0x1FF;

  // Register-programmed parameters.
  internal int Multiple;        // MUL 0..15
  internal int Ksr;             // key-scale-of-rate select (0 or 1) — reg bit
  internal bool EgSustain;      // EG-type: true = sustaining (hold at SL), false = percussive
  internal bool Vibrato;        // PM enable
  internal bool Tremolo;        // AM enable
  internal int Ksl;             // KSL field 0..3 (reg 0x40 bits 6-7)
  internal int TotalLevel;      // TL 0..63 (reg 0x40 bits 0-5)
  internal int AttackRate;      // AR 0..15
  internal int DecayRate;       // DR 0..15
  internal int SustainLevel;    // SL 0..15
  internal int ReleaseRate;     // RR 0..15
  internal int Waveform;        // WS 0..7

  // Runtime state.
  internal uint Phase;          // 0.10 fixed-point phase accumulator scaled <<9 for fraction
  internal EgState State = EgState.Release;
  internal int EnvLevel = MaxAttenuation;   // attenuation in 1/256-dB steps (0 = loud)
  internal int Output;          // last linear output (signed)
  internal int Prev;            // one-sample-old output (for feedback averaging)

  // Owning channel back-reference for F-num/block/key-scale.
  internal OplChannel Channel = null!;

  internal void KeyOn() {
    this.Phase = 0;
    this.State = EgState.Attack;
    // A maxed attack rate (after KSR) snaps the level immediately to peak; handled in stepping.
  }

  internal void KeyOff() {
    if (this.State != EgState.Release)
      this.State = EgState.Release;
  }

  // The "key code" used by KSR: block*2 + the F-num MSB(s) selected by the chip's NTS bit.
  internal int KeyScaleNumber(bool nts) {
    var fnum = this.Channel.FNum;
    var notesel = nts ? ((fnum >> 9) & 1) : ((fnum >> 8) & 1);
    return (this.Channel.Block << 1) | notesel;
  }

  internal int EffectiveRate(int rate, bool nts) {
    if (rate == 0)
      return 0;
    var ksn = this.KeyScaleNumber(nts);
    var rof = this.Ksr != 0 ? ksn : (ksn >> 2);
    var r = (rate << 2) + rof;
    return r > 63 ? 63 : r;
  }

  // KSL attenuation in 1/256-dB steps for the current F-num/block.
  internal int KslAttenuation() {
    if (this.Ksl == 0)
      return 0;
    var hi = (this.Channel.FNum >> 6) & 0x0F;
    var att = OplTables.KslTable[hi] - ((8 - this.Channel.Block) << 5);
    if (att < 0)
      att = 0;
    return att >> OplTables.KslShift[this.Ksl];
  }
}
