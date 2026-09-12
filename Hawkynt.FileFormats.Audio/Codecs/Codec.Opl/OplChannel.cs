#pragma warning disable CS1591
namespace Codec.Opl;

/// <summary>
/// One OPL channel: a pair of operators (modulator + carrier) plus the per-channel F-number,
/// block, feedback, connection (FM vs additive) and — on OPL3 — the L/R panning bits and 4-op
/// pairing. Mirrors <c>Nuked-OPL3</c>'s <c>opl3_channel</c>.
/// </summary>
internal sealed class OplChannel {

  internal readonly OplOperator Modulator = new();
  internal readonly OplOperator Carrier = new();

  internal int FNum;            // 10-bit F-number
  internal int Block;           // 0..7 octave
  internal int Feedback;        // 0..7 modulator feedback shift
  internal bool Additive;       // connection bit (reg 0xC0 bit0): true = AM/additive, false = FM
  internal bool KeyOn;

  // OPL3 panning (reg 0xC0 bits 4-5): default both enabled (OPL/OPL2 are mono → both sides).
  internal bool Left = true;
  internal bool Right = true;

  // 4-operator mode (OPL3): when true this channel is the *first* of a 4-op pair and drives all
  // four operators; the partner (channel+3) is suppressed as an independent 2-op voice.
  internal bool FourOp;
  internal bool FourOpSecondary;   // the partner half of an active 4-op pair (silenced as 2-op)
  internal OplChannel? Partner;    // the channel+3 (or channel-3) paired in 4-op mode
}
