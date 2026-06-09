#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.HuC6280;

namespace Compression.Tests.Codecs.HuC6280;

[TestFixture]
public class HesPlayerTests {

  // Builds a HES file: HESM header (init at $E000) + one DATA block loaded at physical $E000 (page
  // $70). The init program maps MPR7→$70 (its own code) and MPR0→$FF (the I/O page so $0800 reaches
  // the PSG), programs a square-wave tone on PSG channel 0, then RTS.
  private static byte[] BuildToneHes() {
    // ── init program, assembled at physical $E000 (logical $E000 via MPR7) ──
    var prog = new List<byte>();
    void Lda(byte v) { prog.Add(0xA9); prog.Add(v); }
    void Sta(ushort addr) { prog.Add(0x8D); prog.Add((byte)addr); prog.Add((byte)(addr >> 8)); }
    void Tam(byte mask) { prog.Add(0x53); prog.Add(mask); }

    // MPR0 ← $FF (I/O page at logical $0000-$1FFF, so $0800 hits the PSG).
    Lda(0xFF); Tam(0x01);
    // Select PSG channel 0.
    Lda(0x00); Sta(0x0800);
    // Frequency period (low/high).
    Lda(0x50); Sta(0x0802);
    Lda(0x00); Sta(0x0803);
    // Overall volume, not yet enabled.
    Lda(0x1F); Sta(0x0804);
    // Write a 32-step square waveform.
    for (var i = 0; i < 32; ++i) {
      Lda((byte)(i < 16 ? 31 : 0));
      Sta(0x0806);
    }
    // L/R volume max.
    Lda(0xFF); Sta(0x0805);
    // Enable channel + overall volume.
    Lda(0x9F); Sta(0x0804);
    // Global L/R balance max.
    Lda(0xFF); Sta(0x0801);
    prog.Add(0x60); // RTS

    var program = prog.ToArray();

    // ── HES file: header + DATA block at physical $E000 ──
    var ms = new MemoryStream();
    var header = new byte[0x10];
    "HESM"u8.CopyTo(header);
    header[0x04] = 0;                                    // version
    header[0x05] = 0;                                    // first song (0-based)
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x06), 0xE000); // init addr
    // Initial MPR: identity map (MPR_n → physical page n) so logical $E000 (MPR7) maps to
    // physical page 7 = physical $E000, where the DATA block is loaded. MPR1 → physical page $F8
    // (work RAM) so the stack at logical $2100 reaches RAM, as on a real PC Engine.
    for (var i = 0; i < 8; ++i) header[0x08 + i] = (byte)i;
    header[0x08 + 1] = 0xF8;
    ms.Write(header);

    var bh = new byte[0x10];
    "DATA"u8.CopyTo(bh);
    BinaryPrimitives.WriteUInt32LittleEndian(bh.AsSpan(4), (uint)program.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(bh.AsSpan(8), 0xE000); // physical load addr (page 7)
    ms.Write(bh);
    ms.Write(program);
    return ms.ToArray();
  }

  [Test]
  public void Player_RendersNonzeroPcm() {
    var hes = BuildToneHes();
    var player = new HesPlayer(hes, song: 0, outputRate: 44100);
    var stereo = player.RenderStereo(0.5);
    var peak = stereo.Max(x => Math.Abs((int)x));
    Assert.That(peak, Is.GreaterThan(100), "a tone-programming HES must render nonzero PCM");
  }

  [Test]
  public void Player_FrameRateIsNtsc60Hz() {
    var player = new HesPlayer(BuildToneHes(), song: 0, outputRate: 44100);
    Assert.That(player.FrameRateHz, Is.EqualTo(60.0).Within(0.01));
  }

  [Test]
  public void Player_RejectsShortFile() {
    Assert.Throws<NotSupportedException>(() => new HesPlayer("HESM"u8.ToArray()));
  }

  [Test]
  public void Player_RejectsNonHes() {
    var notHes = new byte[0x20];
    "JUNK"u8.CopyTo(notHes);
    Assert.Throws<NotSupportedException>(() => new HesPlayer(notHes));
  }
}
