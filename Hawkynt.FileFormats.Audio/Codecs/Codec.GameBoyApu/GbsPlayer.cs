#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.GameBoyApu;

/// <summary>
/// A GBS (Game Boy Sound) tune player. It maps the tune's program image into a 64 KB Game Boy
/// address space, runs the tune's init routine for a chosen song, then repeatedly calls the
/// play routine at the tune's frame rate, rendering Game Boy APU samples between frames.
/// <para>Memory map: the GBS data is exposed as banked ROM — the bytes from <c>loadAddr</c>
/// onward form a series of 16 KB banks. Bank 0 sits at $0000-$3FFF and the currently selected
/// bank sits at $4000-$7FFF. The selected bank is changed by a write to $2000-$3FFF (the MBC
/// ROM-bank-select region); bank 0 maps to bank 1 (a quirk of MBC1, matching the GBS spec).
/// Work RAM $A000-$DFFF and high RAM $FF80-$FFFE are read/write. The sound register window
/// $FF10-$FF3F is captured into the <see cref="GameBoyApu"/>. The timer registers $FF04-$FF07
/// are honoured to derive the play rate when the tune requests timer-driven playback.</para>
/// <para>The play rate is resolved from the header timer control: when bit 2 of
/// <c>timerControl</c> is set the rate is timer-driven — <c>baseFreq / (256 - timerModulo)</c>
/// with the base selected by the low two bits of <c>timerControl</c> from {4096, 262144,
/// 65536, 16384} Hz — otherwise the ~59.7 Hz VBlank rate is used. As in the SID player, APU
/// register writes take effect immediately when the CPU performs them; cycle-accurate ordering
/// of writes against sample rendering is not modelled.</para>
/// </summary>
public sealed class GbsPlayer {

  private sealed class Bus : ISm83Bus {
    private readonly byte[] _rom;        // GBS data starting at loadAddr's bank 0
    private readonly ushort _loadAddr;
    private readonly int _bankCount;
    private readonly byte[] _ram = new byte[0x2000];   // $A000-$BFFF (cart RAM) — also covers...
    private readonly byte[] _wram = new byte[0x2000];  // $C000-$DFFF work RAM
    private readonly byte[] _hram = new byte[0x7F];    // $FF80-$FFFE
    private readonly GameBoyApu _apu;
    private int _romBank = 1;

    public byte[] TimerRegisters { get; } = new byte[4]; // $FF04-$FF07

    public Bus(byte[] rom, ushort loadAddr, GameBoyApu apu) {
      this._rom = rom;
      this._loadAddr = loadAddr;
      this._apu = apu;
      this._bankCount = Math.Max(1, (rom.Length + 0x3FFF) / 0x4000);
    }

    // Reads a byte from the GBS data as if it were mapped from loadAddr: bank 0 occupies the
    // first 16 KB of data, but the data only begins at loadAddr within the $0000-$3FFF window.
    private byte ReadRom(int bank, int offsetInBank) {
      // The first bank's content starts at loadAddr; addresses below loadAddr read as zero.
      var dataIndex = bank == 0
        ? offsetInBank - this._loadAddr
        : (bank % this._bankCount) * 0x4000 + offsetInBank;
      if (dataIndex < 0 || dataIndex >= this._rom.Length)
        return 0;
      return this._rom[dataIndex];
    }

    public byte Read(ushort addr) {
      switch (addr) {
        case <= 0x3FFF: return this.ReadRom(0, addr);
        case <= 0x7FFF: return this.ReadRom(this._romBank, addr - 0x4000);
        case >= 0xA000 and <= 0xBFFF: return this._ram[addr - 0xA000];
        case >= 0xC000 and <= 0xDFFF: return this._wram[addr - 0xC000];
        case >= 0xE000 and <= 0xFDFF: return this._wram[addr - 0xE000]; // echo RAM
        case >= 0xFF10 and <= 0xFF3F: return this._apu.Read(addr);
        case >= 0xFF04 and <= 0xFF07: return this.TimerRegisters[addr - 0xFF04];
        case >= 0xFF80 and <= 0xFFFE: return this._hram[addr - 0xFF80];
        default: return 0xFF;
      }
    }

    public void Write(ushort addr, byte value) {
      switch (addr) {
        case >= 0x2000 and <= 0x3FFF:
          // MBC ROM-bank select: bank 0 is remapped to 1.
          var bank = value & 0x7F;
          this._romBank = bank == 0 ? 1 : bank;
          break;
        case >= 0xA000 and <= 0xBFFF: this._ram[addr - 0xA000] = value; break;
        case >= 0xC000 and <= 0xDFFF: this._wram[addr - 0xC000] = value; break;
        case >= 0xE000 and <= 0xFDFF: this._wram[addr - 0xE000] = value; break;
        case >= 0xFF10 and <= 0xFF3F: this._apu.Write(addr, value); break;
        case >= 0xFF04 and <= 0xFF07: this.TimerRegisters[addr - 0xFF04] = value; break;
        case >= 0xFF80 and <= 0xFFFE: this._hram[addr - 0xFF80] = value; break;
        default: break;
      }
    }
  }

  // Game Boy timer input frequencies selected by the low two bits of the timer control byte.
  private static readonly double[] TimerBaseFreq = [4096.0, 262144.0, 65536.0, 16384.0];

  private readonly GameBoyApu _apu;
  private readonly Bus _bus;
  private readonly Sm83Cpu _cpu;
  private readonly ushort _playAddr;
  private readonly ushort _stackPtr;
  private readonly double _frameRateHz;

  /// <summary>The resolved play rate (calls per second) for this tune.</summary>
  public double FrameRateHz => this._frameRateHz;

  /// <summary>The output sample rate of the rendered audio.</summary>
  public int OutputSampleRate { get; }

  /// <summary>
  /// Builds a player from the full GBS file bytes. The header (0x70 bytes) is parsed here.
  /// <paramref name="song"/> is 0-based (the GBS init convention passes the song number in
  /// the A register); pass <c>header.firstSong - 1</c> to start at the tune's default song.
  /// </summary>
  public GbsPlayer(byte[] file, int song = 0, int outputRate = GameBoyApu.OutputSampleRate) {
    const int headerSize = 0x70;
    if (file.Length < headerSize)
      throw new NotSupportedException("GBS file too short to contain a header.");
    if (file[0] != 0x47 || file[1] != 0x42 || file[2] != 0x53)
      throw new NotSupportedException("Not a GBS file (missing 'GBS' magic).");

    var loadAddr = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x06));
    var initAddr = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x08));
    this._playAddr = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x0A));
    this._stackPtr = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x0C));
    var timerModulo = file[0x0E];
    var timerControl = file[0x0F];

    this.OutputSampleRate = outputRate;
    this._apu = new GameBoyApu(outputRate);

    var program = file[headerSize..];
    this._bus = new Bus(program, loadAddr, this._apu);
    this._cpu = new Sm83Cpu(this._bus);

    // Power the APU on so channels can be triggered (NR52 bit 7).
    this._apu.Write(0xFF26, 0x80);

    // Init: SP from header, A = song number (0-based). The GBS spec also zeroes the other
    // registers; A carries the song selector.
    this._cpu.SP = this._stackPtr;
    this._cpu.A = (byte)song;
    this._cpu.B = this._cpu.C = this._cpu.D = this._cpu.E = this._cpu.H = this._cpu.L = 0;
    this._cpu.RunUntilRet(initAddr, 2_000_000);

    this._frameRateHz = ResolveFrameRate(timerControl, timerModulo);
  }

  private static double ResolveFrameRate(byte timerControl, byte timerModulo) {
    // Bit 2 set → timer-driven playback; low two bits select the base frequency.
    if ((timerControl & 0x04) != 0) {
      var baseFreq = TimerBaseFreq[timerControl & 0x03];
      var divisor = 256 - timerModulo;
      if (divisor <= 0)
        divisor = 256;
      return baseFreq / divisor;
    }
    // VBlank: the DMG vertical-blank rate.
    return 59.7;
  }

  /// <summary>Renders <paramref name="seconds"/> of audio as interleaved 16-bit stereo PCM.</summary>
  public short[] Render(double seconds) {
    var totalFrames = (int)(seconds * this.OutputSampleRate);
    var result = new short[totalFrames * 2];

    var framesPerTick = this.OutputSampleRate / this._frameRateHz;
    var produced = 0;
    var tickAccumulator = 0.0;
    // Generous per-call cycle cap (one VBlank frame is ~70k cycles at 4.19 MHz / 59.7 Hz).
    var maxCyclesPerTick = (long)(GameBoyApu.ClockHz / this._frameRateHz * 4);

    while (produced < totalFrames) {
      if (this._playAddr != 0) {
        // Restore SP each tick: GBS play routines assume a fresh stack from the header.
        this._cpu.SP = this._stackPtr;
        this._cpu.RunUntilRet(this._playAddr, maxCyclesPerTick);
      }

      tickAccumulator += framesPerTick;
      var thisTick = (int)tickAccumulator;
      tickAccumulator -= thisTick;
      if (produced + thisTick > totalFrames)
        thisTick = totalFrames - produced;
      if (thisTick > 0) {
        this._apu.RenderSamples(result.AsSpan(produced * 2, thisTick * 2), thisTick);
        produced += thisTick;
      }
    }
    return result;
  }
}
