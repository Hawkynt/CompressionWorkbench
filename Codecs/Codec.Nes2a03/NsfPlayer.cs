#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Mos6502;

namespace Codec.Nes2a03;

/// <summary>
/// An NSF (NES Sound Format) tune player. It maps the tune's 6502 program into a 64 KB bus,
/// runs the tune's init routine for a chosen song, then repeatedly calls the play routine at
/// the tune's frame rate, rendering 2A03 APU samples between calls.
/// <para>The bus is RAM at $0000-$07FF (mirrored through $1FFF), the APU register window
/// $4000-$4017 (writes captured into the <see cref="Apu2a03"/>, $4015 readable), the
/// bankswitch registers $5FF8-$5FFF (active only on bankswitched tunes), WRAM $6000-$7FFF
/// and the program area $8000-$FFFF. On a non-bankswitched tune the program is loaded
/// straight at <c>loadAddr</c>; on a bankswitched tune the NSF data is sliced into 4 KB banks
/// mapped at $8000-$FFFF, with the eight bank registers initialised from the header's
/// bankswitch bytes.</para>
/// <para>Only the base 2A03 is emulated; a tune declaring any expansion chip
/// (VRC6/VRC7/FDS/MMC5/N163/S5B) is rejected with <see cref="NotSupportedException"/>. NSFE
/// containers are not parsed here — construct from the parsed NESM fields instead.</para>
/// </summary>
public sealed class NsfPlayer {

  /// <summary>Default render rate.</summary>
  public const int OutputSampleRate = Apu2a03.OutputSampleRate;

  private sealed class Bus : IBus6502 {
    private readonly byte[] _ram = new byte[0x10000];
    private readonly byte[] _data;          // raw NSF program data (for bankswitching)
    private readonly bool _bankswitched;
    private readonly int _bankBaseOffset;   // offset of the byte mapped at $8000 bank 0 boundary
    private readonly int[] _bankMap = new int[8]; // 4 KB bank index mapped at $8000+i*0x1000

    public Apu2a03? Apu;

    public Bus(byte[] data, bool bankswitched, int bankBaseOffset) {
      this._data = data;
      this._bankswitched = bankswitched;
      this._bankBaseOffset = bankBaseOffset;
    }

    public byte[] Ram => this._ram;

    public void SetBank(int slot, int bank) {
      this._bankMap[slot & 0x07] = bank;
    }

    public byte Read(ushort addr) {
      if (this._bankswitched && addr >= 0x8000) {
        var slot = (addr - 0x8000) >> 12;
        var offsetInBank = addr & 0x0FFF;
        var srcIndex = this._bankBaseOffset + this._bankMap[slot] * 0x1000 + offsetInBank;
        return srcIndex >= 0 && srcIndex < this._data.Length ? this._data[srcIndex] : (byte)0;
      }
      if (addr == 0x4015)
        return this.Apu?.Read4015() ?? 0;
      // RAM $0000-$07FF mirrors through $1FFF; everything else read directly.
      return addr < 0x2000 ? this._ram[addr & 0x07FF] : this._ram[addr];
    }

    public void Write(ushort addr, byte value) {
      if (addr is >= 0x4000 and <= 0x4017) {
        this.Apu?.Write(addr, value);
        // Mirror into RAM too so reads of write-only registers are harmless.
        this._ram[addr] = value;
        return;
      }
      if (this._bankswitched && addr is >= 0x5FF8 and <= 0x5FFF) {
        this.SetBank(addr - 0x5FF8, value);
        return;
      }
      // RAM $0000-$07FF mirrors through $1FFF; everything else stored directly.
      if (addr < 0x2000)
        this._ram[addr & 0x07FF] = value;
      else
        this._ram[addr] = value;
    }
  }

  private readonly Bus _bus;
  private readonly Apu2a03 _apu;
  private readonly Cpu6502 _cpu;
  private readonly ushort _initAddr;
  private readonly ushort _playAddr;
  private readonly double _frameRateHz;
  private readonly double _clockHz;

  /// <summary>The resolved frame rate (play calls per second) for this tune.</summary>
  public double FrameRateHz => this._frameRateHz;

  /// <summary>The 2A03 APU instance driving synthesis.</summary>
  public Apu2a03 Apu => this._apu;

  /// <summary>
  /// Builds a player from the parsed NESM fields plus the program data (the bytes after the
  /// 0x80-byte NESM header). <paramref name="useNtsc"/> selects the region (clock and the
  /// init X register). <paramref name="playSpeedMicros"/> is the per-frame interval in
  /// microseconds from the header (NTSC or PAL speed word); a zero or unset value falls back
  /// to the region's vertical-blank rate.
  /// </summary>
  public NsfPlayer(
      byte[] programData,
      ushort loadAddr,
      ushort initAddr,
      ushort playAddr,
      int startSong,
      byte extraChips,
      bool useNtsc,
      int playSpeedMicros,
      ReadOnlySpan<byte> bankswitchBytes) {

    if (extraChips != 0)
      throw new NotSupportedException(
        $"NSF expansion chip(s) 0x{extraChips:X2} ({DescribeChips(extraChips)}) are not supported.");

    var bankswitched = false;
    for (var i = 0; i < bankswitchBytes.Length && i < 8; ++i)
      if (bankswitchBytes[i] != 0)
        bankswitched = true;

    this._clockHz = useNtsc ? Apu2a03.NtscClockHz : Apu2a03.PalClockHz;
    this._initAddr = initAddr;
    this._playAddr = playAddr;

    // On a bankswitched tune, loadAddr's low 12 bits give the offset of the program data's
    // first byte within bank 0; data before that fills the start of the bank window.
    var bankBaseOffset = bankswitched ? -(loadAddr & 0x0FFF) : 0;
    this._bus = new Bus(programData, bankswitched, bankBaseOffset);
    this._apu = new Apu2a03(this._bus, this._clockHz);
    this._bus.Apu = this._apu;
    this._cpu = new Cpu6502(this._bus);

    if (bankswitched) {
      for (var i = 0; i < 8; ++i)
        this._bus.SetBank(i, i < bankswitchBytes.Length ? bankswitchBytes[i] : 0);
    } else {
      // Load program straight at loadAddr.
      for (var i = 0; i < programData.Length && loadAddr + i <= 0xFFFF; ++i)
        this._bus.Ram[loadAddr + i] = programData[i];
    }

    this._frameRateHz = ResolveFrameRate(playSpeedMicros, useNtsc);

    // Init: A = (song - 1), X = 0 (NTSC) / 1 (PAL). startSong is 1-based.
    var song = startSong < 1 ? 0 : startSong - 1;
    this._cpu.A = (byte)song;
    this._cpu.X = (byte)(useNtsc ? 0 : 1);
    this._cpu.Y = 0;
    this._cpu.RunUntilRts(this._initAddr, 1_000_000);
  }

  private static double ResolveFrameRate(int playSpeedMicros, bool useNtsc) {
    if (playSpeedMicros > 0)
      return 1_000_000.0 / playSpeedMicros;
    return useNtsc ? 60.0 : 50.0;
  }

  /// <summary>Renders <paramref name="seconds"/> of audio as mono 16-bit PCM at <paramref name="outputRate"/>.</summary>
  public short[] Render(double seconds, int outputRate = OutputSampleRate) {
    var totalSamples = (int)(seconds * outputRate);
    var result = new short[totalSamples];

    var samplesPerFrame = outputRate / this._frameRateHz;
    var produced = 0;
    var frameAccumulator = 0.0;
    var maxCyclesPerFrame = (long)(this._clockHz / this._frameRateHz * 8);

    while (produced < totalSamples) {
      if (this._playAddr != 0)
        this._cpu.RunUntilRts(this._playAddr, maxCyclesPerFrame);

      frameAccumulator += samplesPerFrame;
      var thisFrame = (int)frameAccumulator;
      frameAccumulator -= thisFrame;
      if (produced + thisFrame > totalSamples)
        thisFrame = totalSamples - produced;
      if (thisFrame > 0) {
        this._apu.RenderSamples(result.AsSpan(produced, thisFrame), thisFrame);
        produced += thisFrame;
      }
    }
    return result;
  }

  /// <summary>Builds a player directly from full NESM file bytes (0x80 header + program).</summary>
  public static NsfPlayer FromNesm(byte[] file) {
    const int headerSize = 0x80;
    if (file.Length < headerSize)
      throw new NotSupportedException("NSF file too short to contain a NESM header.");

    var startSong = file[0x07];
    var loadAddr = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x08));
    var initAddr = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x0A));
    var playAddr = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x0C));
    var ntscSpeed = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x6E));
    var palSpeed = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x78));
    var palNtscFlags = file[0x7A];
    var extraChips = file[0x7B];

    var useNtsc = (palNtscFlags & 0x01) == 0;
    var playSpeed = useNtsc ? ntscSpeed : palSpeed;
    var program = file[headerSize..];

    return new NsfPlayer(
      program, loadAddr, initAddr, playAddr, startSong, extraChips,
      useNtsc, playSpeed, file.AsSpan(0x70, 8));
  }

  private static string DescribeChips(byte flags) {
    var chips = new List<string>();
    if ((flags & 0x01) != 0) chips.Add("VRC6");
    if ((flags & 0x02) != 0) chips.Add("VRC7");
    if ((flags & 0x04) != 0) chips.Add("FDS");
    if ((flags & 0x08) != 0) chips.Add("MMC5");
    if ((flags & 0x10) != 0) chips.Add("N163");
    if ((flags & 0x20) != 0) chips.Add("S5B");
    return chips.Count > 0 ? string.Join(", ", chips) : "unknown";
  }
}
