#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Mos6502;

namespace Codec.Sid;

/// <summary>
/// A PSID tune player. It loads the C64 program into a 64 KB RAM image, runs the tune's
/// init routine for a chosen song, then repeatedly calls the play routine at the tune's
/// frame rate, rendering SID samples between frames.
/// <para>The memory bus is RAM everywhere except the SID register windows (writes are
/// captured into the matching <see cref="SidChip"/>) and the CIA #1 timer-A registers
/// $DC04/$DC05 (captured to derive the CIA frame rate). SID register writes are applied
/// immediately when the CPU performs them; cycle-accurate ordering of writes against sample
/// rendering is NOT modelled — all writes for a frame take effect before that frame's samples
/// are produced. This is adequate for the steady playback of non-sampled tunes.</para>
/// <para>Multi-SID: a 2SID/3SID tune declares its extra chips through the PSID v3/v4
/// secondSIDAddress/thirdSIDAddress header bytes. SID #1 always lives at $D400; each extra
/// chip occupies its own 32-byte register window inside $D400-$DFFF, and the bus routes a
/// write to whichever chip owns the address. Writes to $D400-$DFFF that fall outside any
/// configured chip's window are ignored by the SID side (still stored to RAM).</para>
/// <para>RSID files, and PSID v2+ tunes flagged as needing the C64 BASIC/KERNAL environment,
/// are rejected with <see cref="NotSupportedException"/>.</para>
/// </summary>
public sealed class PsidPlayer {

  private sealed class Bus : IBus6502 {
    private readonly byte[] _ram = new byte[0x10000];

    // Each chip with the base address of its 32-byte register window ($D400, $Dxx0, …).
    private readonly (ushort Base, SidChip Chip)[] _chips;

    public ushort CiaTimer;
    public bool CiaTimerWritten;

    public Bus((ushort Base, SidChip Chip)[] chips) => this._chips = chips;

    public byte[] Ram => this._ram;

    public byte Read(ushort addr) => this._ram[addr];

    public void Write(ushort addr, byte value) {
      this._ram[addr] = value;

      // Route to the chip whose 32-byte register window contains the address. Extra chips
      // (SID #2/#3) are matched first by their declared $Dxx0 window. SID #1 owns the classic
      // $D400-$D7FF block, mirrored every 32 bytes, EXCEPT any address an extra chip claims —
      // this keeps single-SID mirroring behaviour and routes a $D4xx-window write to whichever
      // chip really sits there. Addresses outside every window are NOT SID writes; in particular
      // the CIA timer registers ($DC04/$DC05) fall through even though they share the page.
      for (var i = 1; i < this._chips.Length; ++i) {
        var (baseAddr, chip) = this._chips[i];
        if (addr >= baseAddr && addr < baseAddr + 0x20) {
          chip.Write(addr - baseAddr, value);
          return;
        }
      }
      if (addr is >= 0xD400 and <= 0xD7FF) {
        this._chips[0].Chip.Write(addr & 0x1F, value);
        return;
      }

      if (addr == 0xDC04) {
        this.CiaTimer = (ushort)((this.CiaTimer & 0xFF00) | value);
        this.CiaTimerWritten = true;
      } else if (addr == 0xDC05) {
        this.CiaTimer = (ushort)((this.CiaTimer & 0x00FF) | (value << 8));
        this.CiaTimerWritten = true;
      }
    }
  }

  private readonly (ushort Base, SidChip Chip)[] _chips;
  private readonly Bus _bus;
  private readonly Cpu6502 _cpu;
  private readonly ushort _initAddr;
  private readonly ushort _playAddr;
  private readonly double _frameRateHz;
  private readonly double _clockHz;

  /// <summary>The resolved frame rate (calls per second) for this tune.</summary>
  public double FrameRateHz => this._frameRateHz;

  /// <summary>The number of SID chips this player drives (1 = mono, 2 = stereo, 3 = 3SID).</summary>
  public int SidCount => this._chips.Length;

  /// <summary>The model in use by SID #1.</summary>
  public SidModel Model => this._chips[0].Chip.Model;

  /// <summary>The model in use by the chip at the given index (0 = SID #1).</summary>
  public SidModel ModelOf(int chip) => this._chips[chip].Chip.Model;

  /// <summary>
  /// Builds a single-SID player. The caller supplies the <paramref name="model"/> and
  /// <paramref name="clockHz"/> already decoded from the descriptor, falling back to 6581/PAL
  /// when unknown.
  /// </summary>
  public PsidPlayer(byte[] file, SidModel model, double clockHz)
    : this(file, [new SidChipConfig(0xD400, model)], clockHz) { }

  /// <summary>
  /// Builds a multi-SID player from explicit per-chip configurations. The first config is SID #1
  /// (its base is forced to $D400 regardless of what is supplied). Each config carries the chip's
  /// register-window base address and its resolved model.
  /// </summary>
  public PsidPlayer(byte[] file, IReadOnlyList<SidChipConfig> chips, double clockHz) {
    if (chips.Count < 1)
      throw new ArgumentException("At least one SID chip is required.", nameof(chips));
    if (file.Length < 0x76)
      throw new NotSupportedException("SID file too short to contain a PSID header.");

    var magic = System.Text.Encoding.ASCII.GetString(file, 0, 4);
    var dataOffset = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x06));
    var loadAddr = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x08));
    this._initAddr = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x0A));
    this._playAddr = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x0C));
    var version = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x04));
    var startSong = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x10));
    var speed = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(0x12));

    if (magic == "RSID")
      throw new NotSupportedException("RSID tunes require a full C64 environment and are not supported.");

    var flags = version >= 2 && file.Length >= 0x7C
      ? BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x76))
      : (ushort)0;
    // PSID v2+ flag bit 1 set means the tune is a C64 BASIC program (no machine-code init).
    if ((flags & 0x02) != 0)
      throw new NotSupportedException("PSID BASIC tunes are not supported.");

    this._clockHz = clockHz;
    this._chips = new (ushort, SidChip)[chips.Count];
    for (var i = 0; i < chips.Count; ++i) {
      var baseAddr = i == 0 ? (ushort)0xD400 : chips[i].BaseAddress;
      this._chips[i] = (baseAddr, new SidChip(chips[i].Model, clockHz));
    }

    this._bus = new Bus(this._chips);
    this._cpu = new Cpu6502(this._bus);

    this.LoadProgram(file, dataOffset, loadAddr);

    // Init: call init with A = (song - 1). startSong is 1-based.
    var song = startSong < 1 ? 0 : startSong - 1;
    this._cpu.A = (byte)song;
    this._cpu.X = (byte)song;
    this._cpu.Y = (byte)song;
    this._cpu.RunUntilRts(this._initAddr, 100_000);

    // Frame rate: speed bit 0 selects CIA timing for song 0.
    var useCia = (speed & 0x01) != 0;
    this._frameRateHz = this.ResolveFrameRate(useCia, clockHz);
  }

  private void LoadProgram(byte[] file, ushort dataOffset, ushort loadAddr) {
    var program = file.AsSpan(Math.Min(dataOffset, file.Length));
    ushort load;
    int skip;
    if (loadAddr == 0) {
      if (program.Length < 2)
        throw new NotSupportedException("SID program too short to carry an embedded load address.");
      load = BinaryPrimitives.ReadUInt16LittleEndian(program);
      skip = 2;
    } else {
      load = loadAddr;
      skip = 0;
    }
    var body = program[skip..];
    for (var i = 0; i < body.Length && (load + i) <= 0xFFFF; ++i)
      this._bus.Ram[load + i] = body[i];
  }

  private double ResolveFrameRate(bool useCia, double clockHz) {
    // Vertical-blank rate from the clock standard: PAL ~0.985 MHz → 50 Hz, NTSC ~1.0227 MHz → 60 Hz.
    var vblank = clockHz < 1_000_000 ? 50.0 : 60.0;
    if (!useCia)
      return vblank;

    // CIA timing: frames = clock / timerValue (the value written during init), else fall back.
    if (this._bus.CiaTimerWritten && this._bus.CiaTimer > 0)
      return clockHz / this._bus.CiaTimer;
    return 60.0;
  }

  /// <summary>
  /// Renders <paramref name="seconds"/> of audio as interleaved mono 16-bit PCM at
  /// <paramref name="outputRate"/> from SID #1 (back-compatible single-chip output).
  /// </summary>
  public short[] Render(double seconds, int outputRate = SidChip.OutputSampleRate)
    => this.RenderPerChip(seconds, outputRate)[0];

  /// <summary>
  /// Renders <paramref name="seconds"/> of audio, returning one mono 16-bit PCM buffer per SID
  /// chip (index 0 = SID #1). All chips are driven by the same program run; only their captured
  /// register writes differ.
  /// </summary>
  public short[][] RenderPerChip(double seconds, int outputRate = SidChip.OutputSampleRate) {
    var totalSamples = (int)(seconds * outputRate);
    var result = new short[this._chips.Length][];
    for (var c = 0; c < this._chips.Length; ++c)
      result[c] = new short[totalSamples];

    var samplesPerFrame = outputRate / this._frameRateHz;
    var produced = 0;
    var frameAccumulator = 0.0;
    // Cap CPU cycles per play call generously (one PAL/NTSC frame is ~20k cycles).
    var maxCyclesPerFrame = (long)(this._clockHz / this._frameRateHz * 4);

    while (produced < totalSamples) {
      // Run one play call (writes hit each SID immediately, routed by the bus).
      if (this._playAddr != 0)
        this._cpu.RunUntilRts(this._playAddr, maxCyclesPerFrame);

      frameAccumulator += samplesPerFrame;
      var thisFrame = (int)frameAccumulator;
      frameAccumulator -= thisFrame;
      if (produced + thisFrame > totalSamples)
        thisFrame = totalSamples - produced;
      if (thisFrame > 0) {
        for (var c = 0; c < this._chips.Length; ++c)
          this._chips[c].Chip.RenderSamples(result[c].AsSpan(produced, thisFrame), thisFrame);
        produced += thisFrame;
      }
    }
    return result;
  }
}

/// <summary>One SID chip's placement and model for a multi-SID <see cref="PsidPlayer"/>.</summary>
/// <param name="BaseAddress">The 32-byte register window base ($D400 for SID #1, $Dxx0 for extras).</param>
/// <param name="Model">The resolved model this chip emulates.</param>
public readonly record struct SidChipConfig(ushort BaseAddress, SidModel Model);
