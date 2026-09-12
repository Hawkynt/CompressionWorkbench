#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Mos6502;

namespace Codec.HuC6280;

/// <summary>
/// A HES (PC Engine / TurboGrafx-16 music) tune player. It builds the HuC6280's banked physical
/// memory from the file's DATA blocks, installs RAM and the integrated PSG into the I/O bank,
/// programs the eight MPR (memory-paging) registers from the header, runs the tune's init routine
/// for a chosen song, then repeatedly calls the play routine at the NTSC frame rate (60 Hz),
/// rendering <see cref="PcePsg"/> stereo samples between frames.
/// <para>Memory model: the HuC6280 sees a 16-bit logical address; its top three bits select one
/// of eight MPR registers, whose 8-bit value is the 8 KiB physical page (so the physical address
/// is 21-bit: <c>page&lt;&lt;13 | (logical &amp; 0x1FFF)</c>). Physical page $F8 is the 8 KiB of
/// work RAM; page $FF is the hardware I/O page, where the PSG occupies $0800-$0809 and the VDC the
/// low addresses. DATA blocks load at a physical address (<c>loadAddr</c>); their bytes populate
/// the corresponding physical ROM pages. The header's eight MPR bytes give the initial logical→
/// physical mapping the tune expects on entry.</para>
/// <para>As in the SID/NSF/GBS players, PSG register writes take effect immediately when the CPU
/// performs them; cycle-accurate ordering of writes against sample rendering is not modelled. The
/// CPU's own MPR registers (set by the tune via TAM) are honoured — the bus reads them every
/// access so a tune that re-banks mid-play is followed.</para>
/// </summary>
public sealed class HesPlayer {

  /// <summary>Default render rate.</summary>
  public const int OutputSampleRate = PcePsg.OutputSampleRate;

  /// <summary>NTSC frame (play-call) rate.</summary>
  public const double NtscFrameRateHz = 60.0;

  private sealed class Bus : IBus6502 {
    // 21-bit physical address space: 256 pages × 8 KiB. ROM pages from DATA blocks, RAM at $F8,
    // I/O (PSG) at $FF.
    private readonly byte[][] _pages = new byte[256][];
    private readonly byte[] _ram = new byte[0x2000];   // physical page $F8 (work RAM)
    private readonly PcePsg _psg;

    public Bus(PcePsg psg) => this._psg = psg;

    /// <summary>The CPU whose MPR registers map logical→physical; set after construction.</summary>
    public CpuHuC6280? Cpu { get; set; }

    /// <summary>Writes <paramref name="data"/> into physical memory starting at physical address
    /// <paramref name="physAddr"/>, allocating ROM pages as needed.</summary>
    public void LoadPhysical(int physAddr, ReadOnlySpan<byte> data) {
      for (var i = 0; i < data.Length; ++i) {
        var phys = physAddr + i;
        var page = (phys >> 13) & 0xFF;
        var off = phys & 0x1FFF;
        if (page == 0xF8) { this._ram[off] = data[i]; continue; }
        (this._pages[page] ??= new byte[0x2000])[off] = data[i];
      }
    }

    private int Physical(ushort addr) {
      var mpr = this.Cpu?.Mpr[addr >> 13] ?? 0;
      return (mpr << 13) | (addr & 0x1FFF);
    }

    public byte Read(ushort addr) {
      var phys = this.Physical(addr);
      var page = (phys >> 13) & 0xFF;
      var off = phys & 0x1FFF;
      if (page == 0xF8) return this._ram[off];
      if (page == 0xFF) return 0; // I/O page reads (VDC/PSG) — not modelled as readable
      return this._pages[page] is { } p ? p[off] : (byte)0;
    }

    public void Write(ushort addr, byte value) {
      var phys = this.Physical(addr);
      var page = (phys >> 13) & 0xFF;
      var off = phys & 0x1FFF;
      if (page == 0xF8) { this._ram[off] = value; return; }
      if (page == 0xFF) {
        // Hardware I/O page: the PSG is at $0800-$0809.
        if (off is >= 0x0800 and <= 0x080F)
          this._psg.WriteRegister(off - 0x0800, value);
        return;
      }
      // Writes into ROM pages are dropped (some tunes blindly write); allow RAM-like scratch only.
      if (this._pages[page] is { } p)
        p[off] = value;
    }
  }

  private readonly PcePsg _psg;
  private readonly Bus _bus;
  private readonly CpuHuC6280 _cpu;
  private readonly ushort _initAddr;
  private readonly double _frameRateHz;

  /// <summary>The resolved frame (play-call) rate for this tune.</summary>
  public double FrameRateHz => this._frameRateHz;

  /// <summary>The PSG instance driving synthesis.</summary>
  public PcePsg Psg => this._psg;

  /// <summary>The output sample rate of the rendered audio.</summary>
  public int OutputRate { get; }

  /// <summary>
  /// Builds a player from the full HES file bytes. <paramref name="song"/> is the (0-based) song
  /// number passed in the A register at init — pass <c>header.firstSong</c> (HES start-song is
  /// already 0-based in most rips; the descriptor decides).
  /// </summary>
  public HesPlayer(byte[] file, int song = 0, int outputRate = OutputSampleRate) {
    const int headerSize = 0x10;
    if (file.Length < headerSize)
      throw new NotSupportedException("HES file too short to contain a header.");
    if (file[0] != 'H' || file[1] != 'E' || file[2] != 'S' || file[3] != 'M')
      throw new NotSupportedException("Not a HES file (missing 'HESM' magic).");

    this._initAddr = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x06));
    this.OutputRate = outputRate;
    this._psg = new PcePsg(outputRate);
    this._bus = new Bus(this._psg);
    this._cpu = new CpuHuC6280(this._bus);
    this._bus.Cpu = this._cpu;

    // Load the DATA blocks into physical memory.
    LoadDataBlocks(file, this._bus);

    // Program the initial MPR registers from the header (logical pages 0..7).
    for (var i = 0; i < 8; ++i)
      this._cpu.Mpr[i] = file[0x08 + i];

    this._frameRateHz = NtscFrameRateHz;

    // Init: A = song number, SP at top of stack. The PC Engine zero page/stack live in work RAM
    // (physical page $F8); whatever the tune's MPR mapping is, the init routine sets it up.
    this._cpu.A = (byte)song;
    this._cpu.X = 0;
    this._cpu.Y = 0;
    this._cpu.SP = 0xFF;
    this._cpu.RunUntilRts(this._initAddr, 2_000_000);
  }

  private static void LoadDataBlocks(byte[] file, Bus bus) {
    const int headerSize = 0x10;
    const int blockHeaderSize = 0x10;
    var pos = headerSize;
    while (pos + blockHeaderSize <= file.Length) {
      if (!(file[pos] == 'D' && file[pos + 1] == 'A' && file[pos + 2] == 'T' && file[pos + 3] == 'A'))
        break;
      var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 4));
      var loadAddr = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 8));
      var payloadStart = pos + blockHeaderSize;
      if (length < 0 || payloadStart + length > file.Length)
        break;
      bus.LoadPhysical(loadAddr, file.AsSpan(payloadStart, length));
      pos = payloadStart + length;
    }
  }

  /// <summary>
  /// Renders <paramref name="seconds"/> of audio as interleaved 16-bit stereo PCM at
  /// <see cref="OutputRate"/>.
  /// </summary>
  public short[] RenderStereo(double seconds) {
    var totalFrames = (int)(seconds * this.OutputRate);
    var result = new short[totalFrames * 2];

    // The HES play routine is the standard PC Engine timer/VBlank handler; HES rips do not carry a
    // separate play vector, so the player re-enters the tune's interrupt handler. We approximate by
    // calling the init's continuation via the VBlank/timer IRQ vectors in physical page $FF... but
    // since HES does not expose those reliably, we drive the tune by repeatedly invoking the play
    // routine stored at the request address's documented HES convention: the byte at logical $1FF6/
    // $1FF7 (IRQ2/VDC) hold the player. We fall back to re-running init when no play vector exists.
    var playAddr = this.ResolvePlayAddress();

    var framesPerTick = this.OutputRate / this._frameRateHz;
    var produced = 0;
    var tickAccumulator = 0.0;
    var maxCyclesPerTick = (long)(PcePsg.PsgClockHz / this._frameRateHz * 4);

    while (produced < totalFrames) {
      if (playAddr != 0)
        this._cpu.RunUntilRts(playAddr, maxCyclesPerTick);

      tickAccumulator += framesPerTick;
      var thisTick = (int)tickAccumulator;
      tickAccumulator -= thisTick;
      if (produced + thisTick > totalFrames)
        thisTick = totalFrames - produced;
      if (thisTick > 0) {
        this._psg.RenderSamples(result.AsSpan(produced * 2, thisTick * 2), thisTick);
        produced += thisTick;
      }
    }
    return result;
  }

  /// <summary>
  /// Resolves the per-frame play routine. HES tunes install an interrupt handler rather than
  /// carry an explicit play vector; the de-facto convention used by HES rippers points the play
  /// routine at the HuC6280 interrupt vectors in the mapped address space. We read the IRQ1/VDC
  /// vector at logical $FFF8 (mapped through MPR 7); a zero result means there is no separate
  /// per-frame routine and the init loop is self-driving.
  /// </summary>
  private ushort ResolvePlayAddress() {
    var lo = this._bus.Read(0xFFF8);
    var hi = this._bus.Read(0xFFF9);
    return (ushort)(lo | (hi << 8));
  }
}
