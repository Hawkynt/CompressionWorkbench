#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Mos6502;

namespace Codec.Sid;

/// <summary>
/// A PSID tune player. It loads the C64 program into a 64 KB RAM image, runs the tune's
/// init routine for a chosen song, then repeatedly calls the play routine at the tune's
/// frame rate, rendering SID samples between frames.
/// <para>The memory bus is RAM everywhere except the SID register window $D400-$D7FF (writes
/// are captured into the <see cref="SidChip"/>) and the CIA #1 timer-A registers $DC04/$DC05
/// (captured to derive the CIA frame rate). SID register writes are applied immediately when
/// the CPU performs them; cycle-accurate ordering of writes against sample rendering is NOT
/// modelled — all writes for a frame take effect before that frame's samples are produced.
/// This is adequate for the steady playback of non-sampled tunes.</para>
/// <para>RSID files, and PSID v2+ tunes flagged as needing the C64 BASIC/KERNAL environment,
/// are rejected with <see cref="NotSupportedException"/>.</para>
/// </summary>
public sealed class PsidPlayer {

  private sealed class Bus : IBus6502 {
    private readonly byte[] _ram = new byte[0x10000];
    private readonly SidChip _sid;

    public ushort CiaTimer;
    public bool CiaTimerWritten;

    public Bus(SidChip sid) => this._sid = sid;

    public byte[] Ram => this._ram;

    public byte Read(ushort addr) => this._ram[addr];

    public void Write(ushort addr, byte value) {
      this._ram[addr] = value;
      if (addr is >= 0xD400 and <= 0xD7FF) {
        // SID register window mirrors every 32 bytes.
        this._sid.Write(addr & 0x1F, value);
      } else if (addr == 0xDC04) {
        this.CiaTimer = (ushort)((this.CiaTimer & 0xFF00) | value);
        this.CiaTimerWritten = true;
      } else if (addr == 0xDC05) {
        this.CiaTimer = (ushort)((this.CiaTimer & 0x00FF) | (value << 8));
        this.CiaTimerWritten = true;
      }
    }
  }

  private readonly SidChip _sid;
  private readonly Bus _bus;
  private readonly Cpu6502 _cpu;
  private readonly ushort _initAddr;
  private readonly ushort _playAddr;
  private readonly double _frameRateHz;
  private readonly double _clockHz;

  /// <summary>The resolved frame rate (calls per second) for this tune.</summary>
  public double FrameRateHz => this._frameRateHz;

  /// <summary>The SID model in use.</summary>
  public SidModel Model => this._sid.Model;

  /// <summary>
  /// Builds a player from the full PSID/RSID file bytes. The header is parsed here; the
  /// caller supplies the <paramref name="model"/> and <paramref name="clockHz"/> already
  /// decoded from the descriptor (header flags), falling back to 6581/PAL when unknown.
  /// </summary>
  public PsidPlayer(byte[] file, SidModel model, double clockHz) {
    if (file.Length < 0x76)
      throw new NotSupportedException("SID file too short to contain a PSID header.");

    var magic = System.Text.Encoding.ASCII.GetString(file, 0, 4);
    var version = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x04));
    var dataOffset = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x06));
    var loadAddr = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x08));
    this._initAddr = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x0A));
    this._playAddr = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x0C));
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
    this._sid = new SidChip(model, clockHz);
    this._bus = new Bus(this._sid);
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

  /// <summary>Renders <paramref name="seconds"/> of audio as interleaved mono 16-bit PCM at <paramref name="outputRate"/>.</summary>
  public short[] Render(double seconds, int outputRate = SidChip.OutputSampleRate) {
    var totalSamples = (int)(seconds * outputRate);
    var result = new short[totalSamples];

    var samplesPerFrame = outputRate / this._frameRateHz;
    var produced = 0;
    var frameAccumulator = 0.0;
    // Cap CPU cycles per play call generously (one PAL/NTSC frame is ~20k cycles).
    var maxCyclesPerFrame = (long)(this._clockHz / this._frameRateHz * 4);

    while (produced < totalSamples) {
      // Run one play call (writes hit the SID immediately).
      if (this._playAddr != 0) {
        this._cpu.RunUntilRts(this._playAddr, maxCyclesPerFrame);
      }

      frameAccumulator += samplesPerFrame;
      var thisFrame = (int)frameAccumulator;
      frameAccumulator -= thisFrame;
      if (produced + thisFrame > totalSamples)
        thisFrame = totalSamples - produced;
      if (thisFrame > 0) {
        this._sid.RenderSamples(result.AsSpan(produced, thisFrame), thisFrame);
        produced += thisFrame;
      }
    }
    return result;
  }
}
