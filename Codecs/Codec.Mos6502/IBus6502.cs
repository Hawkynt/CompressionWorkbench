#pragma warning disable CS1591
namespace Codec.Mos6502;

/// <summary>
/// The memory abstraction the <see cref="Cpu6502"/> core talks to. Every fetch, read and
/// write the CPU performs is routed through this 16-bit address bus, so a host can model
/// RAM, ROM, and memory-mapped I/O (e.g. a SID register window) however it likes.
/// </summary>
public interface IBus6502 {

  /// <summary>Reads one byte from <paramref name="addr"/>.</summary>
  byte Read(ushort addr);

  /// <summary>Writes <paramref name="value"/> to <paramref name="addr"/>.</summary>
  void Write(ushort addr, byte value);
}
