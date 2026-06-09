#pragma warning disable CS1591
namespace Codec.Nes2a03.Expansion;

/// <summary>
/// A Famicom cartridge expansion sound chip wired alongside the base 2A03 APU. The NSF header's
/// expansion flag byte (offset 0x7B) selects which of these the player instantiates; each one
/// captures its own CPU-bus register window, is clocked together with the APU, and contributes a
/// linear output that the mixer sums on top of the 2A03's nonlinear pulse/TND tables.
/// <para>The relative mix levels follow NSFPlay's documented per-chip masters (see each
/// implementation). Output is returned already scaled into the same ~0..1 domain as the 2A03
/// mixer tables so the player can add the terms directly.</para>
/// </summary>
internal interface IExpansionAudio {

  /// <summary>True when <paramref name="addr"/> falls in this chip's register window.</summary>
  bool HandlesWrite(ushort addr);

  /// <summary>Captures a CPU write to one of this chip's registers.</summary>
  void Write(ushort addr, byte value);

  /// <summary>Optional readback (e.g. Namco 163 RAM, FDS wave RAM); returns false when unhandled.</summary>
  bool TryRead(ushort addr, out byte value);

  /// <summary>Advances the chip by one CPU/APU clock tick.</summary>
  void ClockOneCpuCycle();

  /// <summary>Current linear output, pre-scaled into the 2A03 mixer's ~0..1 domain.</summary>
  float Output();
}
