#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Ay8910;
using Codec.Z80;

namespace FileFormat.Ay;

/// <summary>
/// A ZXAYEMUL (<c>.ay</c>) tune player. It builds a 64 KB Z80 RAM image, loads the chosen
/// song's memory blocks, runs the song's init routine and then calls the interrupt routine
/// once per 50 Hz frame, capturing the player's <c>OUT</c> writes to the AY register-select
/// (<c>$FFFD</c>) and data (<c>$BFFD</c>) ports into an <see cref="Ay8910Chip"/>, from which
/// it renders stereo PCM.
/// <para>The song structure is pointer-chased exactly as <see cref="AyFormatDescriptor"/>
/// parses it: the song-data structure at <c>pData</c> carries (at +10) a self-relative
/// pointer to a points block <c>{ stack, init, interrupt }</c> and (at +12) a self-relative
/// pointer to the memory-block list. The player sets <c>SP</c> from the points block, calls
/// init via <see cref="Cpu.RunUntilRet"/>, and per frame either calls the interrupt routine
/// (when non-zero) or — when the interrupt address is zero — re-invokes init as a pragmatic
/// stand-in (most zero-interrupt tunes are driven entirely from init plus an IM-driven RST 38
/// handler that lives in the loaded RAM; calling init each frame keeps the registers fed).</para>
/// <para>The ZX beeper (port <c>$FE</c> bit 4) is intentionally ignored — this player only
/// models the AY PSG. Multi-AY "TurboSound" tunes drive only the first chip.</para>
/// </summary>
public sealed class AyPlayer {

  private sealed class Bus : IBusZ80 {
    private readonly byte[] _ram = new byte[0x10000];
    private readonly Ay8910Chip _ay;
    private int _selectedReg;

    public Bus(Ay8910Chip ay) => this._ay = ay;

    public byte[] Ram => this._ram;

    public byte ReadMem(ushort addr) => this._ram[addr];
    public void WriteMem(ushort addr, byte value) => this._ram[addr] = value;

    public byte ReadIo(ushort port) => 0xFF;

    public void WriteIo(ushort port, byte value) {
      // ZX AY decoding: A14=1,A15=1 selects the register latch ($FFFD), A15=1,A14=0 ($BFFD)
      // writes data to the selected register. Decode on the documented address-line bits so
      // partially-decoded writes still land.
      if ((port & 0xC002) == 0xC000) {
        this._selectedReg = value & 0x0F;
      } else if ((port & 0xC002) == 0x8000) {
        this._ay.WriteReg(this._selectedReg, value);
      }
    }
  }

  private readonly Ay8910Chip _ay;
  private readonly Bus _bus;
  private readonly Cpu _cpu;
  private readonly ushort _initAddr;
  private readonly ushort _interruptAddr;
  private const double FrameRateHz = 50.0;
  private readonly double _clockHz;

  /// <summary>The number of songs the file declares.</summary>
  public int SongCount { get; }

  /// <summary>
  /// Builds a player for <paramref name="songIndex"/> of the AY file <paramref name="blob"/>.
  /// Throws <see cref="NotSupportedException"/> when the file can't be parsed into a runnable
  /// song (no init address, malformed pointers).
  /// </summary>
  public AyPlayer(byte[] blob, int songIndex = 0, double clockHz = Ay8910Chip.ZxSpectrumClock,
      Ay8910Chip.StereoMode stereo = Ay8910Chip.StereoMode.Abc) {
    if (blob.Length < 0x14)
      throw new NotSupportedException("AY file too short to contain a header.");

    var numSongs = blob[0x10] + 1;
    this.SongCount = numSongs;
    if (songIndex < 0 || songIndex >= numSongs)
      throw new NotSupportedException("Song index out of range.");

    var pSongs = ReadPointer(blob, 0x12);
    if (pSongs < 0 || pSongs + songIndex * 4 + 4 > blob.Length)
      throw new NotSupportedException("AY song table is missing or out of range.");

    var pData = ReadPointer(blob, pSongs + songIndex * 4 + 2);
    if (pData < 0 || pData + 14 > blob.Length)
      throw new NotSupportedException("AY song-data structure is missing or out of range.");

    var pPoints = ReadPointer(blob, pData + 10);
    var pAddresses = ReadPointer(blob, pData + 12);

    ushort stack = 0xFFFF, init = 0, interrupt = 0;
    if (pPoints >= 0 && pPoints + 6 <= blob.Length) {
      stack = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(pPoints));
      init = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(pPoints + 2));
      interrupt = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(pPoints + 4));
    }
    if (init == 0)
      throw new NotSupportedException("AY song has no init address.");

    this._clockHz = clockHz;
    this._ay = new Ay8910Chip(clockHz, stereo);
    this._bus = new Bus(this._ay);
    this._cpu = new Cpu(this._bus);

    this.LoadBlocks(blob, pAddresses);

    this._initAddr = init;
    this._interruptAddr = interrupt;

    // Init the Z80 state: SP from the song structure, A = song number, then call init.
    this._cpu.Reset();
    this._cpu.IFF1 = this._cpu.IFF2 = false;
    this._cpu.SP = stack;
    this._cpu.A = (byte)songIndex;
    this._cpu.IY = 0;
    this._cpu.InterruptMode = 1;
    this._cpu.RunUntilRet(init, 2_000_000);
  }

  private void LoadBlocks(byte[] blob, int pAddresses) {
    if (pAddresses < 0)
      return;
    var pos = pAddresses;
    var block = 0;
    while (pos + 6 <= blob.Length) {
      var address = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(pos));
      var length = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(pos + 2));
      if (address == 0 && length == 0)
        break;

      var dataOffset = ReadPointer(blob, pos + 4);
      if (dataOffset >= 0 && length > 0) {
        for (var i = 0; i < length && dataOffset + i < blob.Length && address + i <= 0xFFFF; ++i)
          this._bus.Ram[address + i] = blob[dataOffset + i];
      }

      pos += 6;
      if (++block > 256)
        break;
    }
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
      // Drive the player once per 50 Hz frame.
      if (this._interruptAddr != 0)
        this._cpu.RunUntilRet(this._interruptAddr, maxCyclesPerFrame);
      else
        this._cpu.RunUntilRet(this._initAddr, maxCyclesPerFrame); // pragmatic zero-interrupt stand-in

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

  private static int ReadPointer(byte[] blob, int position) {
    if (position < 0 || position + 2 > blob.Length)
      return -1;
    var rel = (short)BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(position));
    if (rel == 0)
      return -1;
    var target = position + rel;
    return target >= 0 && target < blob.Length ? target : -1;
  }
}
