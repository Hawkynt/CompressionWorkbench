#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Ay8910;
using Codec.Z80;

namespace FileFormat.Kss;

/// <summary>
/// A KSS (<c>KSCC</c>/<c>KSSX</c>) tune player. It builds a 64 KB Z80 RAM image, loads the
/// tune's data at its load address, calls the init routine (with <c>A</c> = song number) and
/// then calls the play routine once per 60 Hz frame, capturing the player's writes to the MSX
/// PSG ports — <c>$A0</c> (register latch) and <c>$A1</c> (data) — into an
/// <see cref="Ay8910Chip"/>, from which it renders stereo PCM.
/// <para>Only the AY/YM PSG is synthesised. When the header's device-flags byte enables an
/// extra chip (FMPAC/MSX-MUSIC, SCC, MSX-AUDIO) those writes are decoded to no-ops, so the
/// rendered audio carries the PSG voices only; the caller surfaces a metadata note. The SCC
/// wavetable channels are not synthesised. Banked KSSX images are handled as a flat load
/// (the start bank is loaded; bank-switch port writes are ignored), which covers unbanked and
/// simple single-bank tunes.</para>
/// </summary>
public sealed class KssPlayer {

  private sealed class Bus : IBusZ80 {
    private readonly byte[] _ram = new byte[0x10000];
    private readonly Ay8910Chip _ay;
    private int _selectedReg;

    public Bus(Ay8910Chip ay) => this._ay = ay;

    public byte[] Ram => this._ram;

    public byte ReadMem(ushort addr) => this._ram[addr];
    public void WriteMem(ushort addr, byte value) => this._ram[addr] = value;

    public byte ReadIo(ushort port) {
      // $A2 reads back the PSG; everything else floats high.
      var low = port & 0xFF;
      return low == 0xA2 ? this._ay.ReadReg(this._selectedReg) : (byte)0xFF;
    }

    public void WriteIo(ushort port, byte value) {
      var low = port & 0xFF;
      switch (low) {
        case 0xA0: this._selectedReg = value & 0x0F; break;   // PSG register latch
        case 0xA1: this._ay.WriteReg(this._selectedReg, value); break; // PSG data
        // $7E/$7F SCC, $7C/$7D FMPAC, $C0/$C1 MSX-AUDIO: decoded to no-ops (not synthesised).
        default: break;
      }
    }
  }

  private readonly Ay8910Chip _ay;
  private readonly Bus _bus;
  private readonly Cpu _cpu;
  private readonly ushort _playAddr;
  private const double FrameRateHz = 60.0;
  private readonly double _clockHz;

  /// <summary>True when the header requests a sound chip this player does not synthesise.</summary>
  public bool HasUnsupportedDevices { get; }

  /// <summary>
  /// Builds a player from KSS file bytes for <paramref name="songIndex"/>. Throws
  /// <see cref="NotSupportedException"/> when the header is too short or carries no init.
  /// </summary>
  public KssPlayer(byte[] blob, int songIndex = 0, double clockHz = Ay8910Chip.MsxClock,
      Ay8910Chip.StereoMode stereo = Ay8910Chip.StereoMode.Mono) {
    if (blob.Length < 0x10)
      throw new NotSupportedException("KSS file too short to contain a header.");

    var isKssx = blob[0] == 'K' && blob[1] == 'S' && blob[2] == 'S' && blob[3] == 'X';
    var loadAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x04));
    var dataLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x06));
    var initAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x08));
    var playAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x0A));
    var extraHeaderLen = blob[0x0E];
    var deviceFlags = blob[0x0F];

    this.HasUnsupportedDevices = deviceFlags != 0;

    var payloadOffset = 0x10;
    if (isKssx && extraHeaderLen > 0 && blob.Length >= 0x10 + extraHeaderLen)
      payloadOffset = 0x10 + extraHeaderLen;

    if (initAddr == 0)
      throw new NotSupportedException("KSS tune has no init address.");

    this._clockHz = clockHz;
    this._playAddr = playAddr;
    this._ay = new Ay8910Chip(clockHz, stereo);
    this._bus = new Bus(this._ay);
    this._cpu = new Cpu(this._bus);

    // Load the data image at the load address (start bank only; flat layout).
    var available = Math.Min(dataLen == 0 ? blob.Length - payloadOffset : dataLen, blob.Length - payloadOffset);
    for (var i = 0; i < available && loadAddr + i <= 0xFFFF; ++i)
      this._bus.Ram[loadAddr + i] = blob[payloadOffset + i];

    // Init: A = song number, SP at top of RAM, IM 1, then call init.
    this._cpu.Reset();
    this._cpu.IFF1 = this._cpu.IFF2 = false;
    this._cpu.SP = 0xF380; // a high RAM stack clear of the BIOS work area
    this._cpu.A = (byte)songIndex;
    this._cpu.InterruptMode = 1;
    this._cpu.RunUntilRet(initAddr, 4_000_000);
  }

  /// <summary>Renders <paramref name="seconds"/> of interleaved stereo 16-bit PCM at 44.1 kHz.</summary>
  public short[] Render(double seconds, int outputRate = Ay8910Chip.OutputSampleRate) {
    var totalFrames = (int)(seconds * outputRate);
    var result = new short[totalFrames * 2];

    var samplesPerFrame = outputRate / FrameRateHz;
    var produced = 0;
    var accumulator = 0.0;
    var maxCyclesPerFrame = (long)(this._clockHz / FrameRateHz * 4);

    while (produced < totalFrames) {
      if (this._playAddr != 0)
        this._cpu.RunUntilRet(this._playAddr, maxCyclesPerFrame);

      accumulator += samplesPerFrame;
      var thisFrame = (int)accumulator;
      accumulator -= thisFrame;
      if (produced + thisFrame > totalFrames)
        thisFrame = totalFrames - produced;
      if (thisFrame > 0) {
        this._ay.RenderSamples(result.AsSpan(produced * 2, thisFrame * 2), thisFrame);
        produced += thisFrame;
      }
    }
    return result;
  }
}
