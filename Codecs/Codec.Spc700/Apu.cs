#pragma warning disable CS1591
namespace Codec.Spc700;

/// <summary>
/// The SNES APU memory map and hardware registers as seen by the <see cref="Spc700Cpu"/>.
/// Owns the 64&#160;KB ARAM, the three hardware timers, the CONTROL register and the bridge
/// to the <see cref="SDsp"/>. Reads and writes to the special page at <c>$00F0-$00FF</c> are
/// trapped here; everything else is plain ARAM.
/// <para><b>I/O ports ($F4-$F7).</b> On real hardware these are the bidirectional mailbox to
/// the main CPU (the SNES 5A22). A standalone SPC player has no 5A22, so a read returns the
/// last value the SPC700 wrote to the same port (loopback). This matches the common practice
/// for offline SPC rendering: drivers that poll a port for a host handshake simply see their
/// own last write, and music playback (which is timer/DSP-driven) is unaffected.</para>
/// <para><b>IPL ROM ($FFC0-$FFFF).</b> When CONTROL bit&#160;7 is set the top 64 bytes read
/// from the canonical SPC700 boot loader image (see <see cref="IplRom"/>); the underlying
/// ARAM is preserved and re-exposed when the bit is cleared. Writes always hit ARAM.</para>
/// </summary>
public sealed class Apu {

    /// <summary>
  /// Defines the ram size constant value.
  /// </summary>
public const int RamSize = 0x10000;

  /// <summary>The 64&#160;KB audio RAM; also the DSP's sample memory.</summary>
  public readonly byte[] Ram = new byte[RamSize];

    /// <summary>
  /// Provides the dsp value.
  /// </summary>
public readonly SDsp Dsp;

  // CPU I/O ports $F4-$F7 loopback latches.
  private readonly byte[] _ports = new byte[4];

  // Three timers. 0 and 1 tick at 8 kHz, 2 at 64 kHz, derived from the 1.024 MHz clock:
  // 1_024_000 / 8_000 = 128 cycles, 1_024_000 / 64_000 = 16 cycles.
  private const int Timer01Divider = 128;
  private const int Timer2Divider = 16;
  private readonly int[] _timerDivider = [Timer01Divider, Timer01Divider, Timer2Divider];
  private readonly int[] _timerStage = new int[3];   // accumulated CPU cycles in the current stage
  private readonly byte[] _timerTarget = new byte[3]; // $FA-$FC (0 means 256)
  private readonly int[] _timerInternal = new int[3]; // counts up to target, then bumps the 4-bit out
  private readonly byte[] _timerOut = new byte[3];     // $FD-$FF, 4-bit, read-clears
  private readonly bool[] _timerEnabled = new bool[3];

  private byte _control;       // $F1
  private bool _iplRomEnabled; // tracks CONTROL bit 7; the IPL ROM is mapped only while it is set

    /// <summary>
  /// Initializes a new instance of <see cref="Apu"/>.
  /// </summary>
public Apu() => this.Dsp = new SDsp(this.Ram);

  // ── memory access ─────────────────────────────────────────────────────────────

    /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
public byte Read(ushort address) {
    switch (address) {
      case 0xF0: return 0;                 // TEST: write-only on hardware; reads as 0 here.
      case 0xF1: return this._control;     // CONTROL
      case 0xF2: return this.Dsp.Address;  // DSP address
      case 0xF3: return this.Dsp.Read();   // DSP data
      case 0xF4: case 0xF5: case 0xF6: case 0xF7:
        return this._ports[address - 0xF4];
      case 0xF8: case 0xF9:
        return this.Ram[address];          // auxiliary RAM regs: plain storage
      case 0xFA: case 0xFB: case 0xFC:
        return 0;                          // timer targets are write-only
      case 0xFD: case 0xFE: case 0xFF: {    // timer outputs: 4-bit, read-clears
        var i = address - 0xFD;
        var v = this._timerOut[i];
        this._timerOut[i] = 0;
        return v;
      }
      default:
        if (this._iplRomEnabled && address >= 0xFFC0)
          return IplRom[address - 0xFFC0];
        return this.Ram[address];
    }
  }

    /// <summary>
  /// Writes the value to the supplied output.
  /// </summary>
public void Write(ushort address, byte value) {
    switch (address) {
      case 0xF0: return;                    // TEST: ignored.
      case 0xF1: this.WriteControl(value); return;
      case 0xF2: this.Dsp.Address = value; return;
      case 0xF3: this.Dsp.Write(value); return;
      case 0xF4: case 0xF5: case 0xF6: case 0xF7:
        this._ports[address - 0xF4] = value; return; // loopback latch
      case 0xF8: case 0xF9:
        this.Ram[address] = value; return;
      case 0xFA: case 0xFB: case 0xFC:
        this._timerTarget[address - 0xFA] = value; return;
      case 0xFD: case 0xFE: case 0xFF:
        return;                             // timer outputs are read-only
      default:
        this.Ram[address] = value;          // IPL ROM region is still backed by ARAM for writes
        return;
    }
  }

  private void WriteControl(byte value) {
    this._control = value;

    // Bits 0/1/2 enable timers 0/1/2; a 0→1 transition resets the timer's internal state.
    for (var i = 0; i < 3; ++i) {
      var enable = (value & (1 << i)) != 0;
      if (enable && !this._timerEnabled[i]) {
        this._timerInternal[i] = 0;
        this._timerStage[i] = 0;
        this._timerOut[i] = 0;
      }
      this._timerEnabled[i] = enable;
    }

    // Bits 4/5 clear the CPU input port pairs ($F4/$F5 and $F6/$F7).
    if ((value & 0x10) != 0) { this._ports[0] = 0; this._ports[1] = 0; }
    if ((value & 0x20) != 0) { this._ports[2] = 0; this._ports[3] = 0; }

    // Bit 7 maps the IPL ROM over $FFC0-$FFFF.
    this._iplRomEnabled = (value & 0x80) != 0;
  }

  // ── timers ──────────────────────────────────────────────────────────────────────

  /// <summary>Advances the three hardware timers by <paramref name="cycles"/> master cycles.</summary>
  public void StepTimers(int cycles) {
    for (var i = 0; i < 3; ++i) {
      if (!this._timerEnabled[i])
        continue;

      this._timerStage[i] += cycles;
      var divider = this._timerDivider[i];
      while (this._timerStage[i] >= divider) {
        this._timerStage[i] -= divider;
        if (++this._timerInternal[i] >= (this._timerTarget[i] == 0 ? 256 : this._timerTarget[i])) {
          this._timerInternal[i] = 0;
          this._timerOut[i] = (byte)((this._timerOut[i] + 1) & 0x0F);
        }
      }
    }
  }

  /// <summary>
  /// Seeds the timer targets and the IPL-ROM mapping bit straight from a loaded ARAM image
  /// (the SPC save state already contains the last values written to $FA-$FC and $F1).
  /// </summary>
  public void InitializeFromRam() {
    for (var i = 0; i < 3; ++i)
      this._timerTarget[i] = this.Ram[0xFA + i];
    this.WriteControl(this.Ram[0xF1]);
  }

  // ── canonical IPL ROM ─────────────────────────────────────────────────────────────

  /// <summary>
  /// The 64-byte SPC700 boot-loader ROM mapped at <c>$FFC0-$FFFF</c>. This is the well-known,
  /// fixed boot program burned into every SNES APU; the bytes are public and identical across
  /// all units. Reproduced here so a tune that re-enables the IPL ROM (CONTROL bit&#160;7) sees
  /// the real boot code rather than stale ARAM.
  /// </summary>
  public static readonly byte[] IplRom = [
    0xCD, 0xEF, 0xBD, 0xE8, 0x00, 0xC6, 0x1D, 0xD0,
    0xFC, 0x8F, 0xAA, 0xF4, 0x8F, 0xBB, 0xF5, 0x78,
    0xCC, 0xF4, 0xD0, 0xFB, 0x2F, 0x19, 0xEB, 0xF4,
    0xD0, 0xFC, 0x7E, 0xF4, 0xD0, 0x0B, 0xE4, 0xF5,
    0xCB, 0xF4, 0xD7, 0x00, 0xFC, 0xD0, 0xF3, 0xAB,
    0x01, 0x10, 0xEF, 0x7E, 0xF4, 0x10, 0xEB, 0xBA,
    0xF6, 0xDA, 0x00, 0xBA, 0xF4, 0xC4, 0xF4, 0xDD,
    0x5D, 0xD0, 0xDB, 0x1F, 0x00, 0x00, 0xC0, 0xFF,
  ];
}
