#pragma warning disable CS1591
namespace Codec.GameBoyApu;

/// <summary>
/// The memory abstraction the <see cref="Sm83Cpu"/> core talks to. Every fetch, read and
/// write the CPU performs is routed through this 16-bit address bus, so a host can model
/// ROM, RAM, HRAM and memory-mapped I/O (the APU register window, timer registers,…)
/// however it likes. Mirrors <c>IBus6502</c> in spirit.
/// </summary>
public interface ISm83Bus {

  /// <summary>Reads one byte from <paramref name="addr"/>.</summary>
  byte Read(ushort addr);

  /// <summary>Writes <paramref name="value"/> to <paramref name="addr"/>.</summary>
  void Write(ushort addr, byte value);
}
