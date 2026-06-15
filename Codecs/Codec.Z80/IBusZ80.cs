#pragma warning disable CS1591
namespace Codec.Z80;

/// <summary>
/// The memory and I/O abstraction the <see cref="Cpu"/> core talks to. Every memory fetch,
/// read and write goes through the 16-bit address bus; every port access goes through the
/// I/O bus. The Z80 places the 16-bit BC pair on the address lines during <c>IN/OUT (C)</c>
/// and the accumulator on the high byte during <c>IN/OUT (n)</c>, so the port address is a
/// full <see cref="ushort"/> here and a host may decode it as narrowly (8-bit) or as widely
/// (16-bit) as the modelled machine requires.
/// </summary>
public interface IBusZ80 {

  /// <summary>Reads one byte from memory at <paramref name="addr"/>.</summary>
  byte ReadMem(ushort addr);

  /// <summary>Writes <paramref name="value"/> to memory at <paramref name="addr"/>.</summary>
  void WriteMem(ushort addr, byte value);

  /// <summary>Reads one byte from the I/O port at <paramref name="port"/>.</summary>
  byte ReadIo(ushort port);

  /// <summary>Writes <paramref name="value"/> to the I/O port at <paramref name="port"/>.</summary>
  void WriteIo(ushort port, byte value);
}
