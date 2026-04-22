#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.GameMaker;

namespace Compression.Tests.GameMaker;

[TestFixture]
public class GameMakerTests {

  private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

  /// <summary>
  /// Builds a tiny FORM file with GEN8 (empty), TXTR (two synthetic PNG blobs),
  /// AUDO (one WAV + one OGG) and STRG (two strings).
  /// </summary>
  private static byte[] MakeMinimalWin() {
    using var body = new MemoryStream();

    // ── GEN8 (just 0x40 zero bytes for layout padding) ───────────────────────
    AppendChunk(body, "GEN8", new byte[0x40]);

    // ── TXTR: two fake PNG blobs, one after another (back-to-back) ───────────
    using (var txtr = new MemoryStream()) {
      // Pointer table (2 entries, placeholder — descriptor scans for PNG magic directly).
      Write32(txtr, 2);
      Write32(txtr, 0);
      Write32(txtr, 0);

      var png1 = BuildSyntheticPng(Encoding.ASCII.GetBytes("PIXEL-DATA-1"));
      var png2 = BuildSyntheticPng(Encoding.ASCII.GetBytes("PIXEL-DATA-2-LONGER"));
      txtr.Write(png1);
      txtr.Write(png2);

      AppendChunk(body, "TXTR", txtr.ToArray());
    }

    // ── AUDO: one WAV + one OGG ───────────────────────────────────────────────
    using (var audo = new MemoryStream()) {
      // Lay out: uint32 count + 2× uint32 offsets + per-entry (uint32 len + data).
      var wav = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVEtestwavdata");
      var ogg = Encoding.ASCII.GetBytes("OggS_fake_ogg_stream_body");

      var ptrArea = 4 + 2 * 4;
      var off1 = ptrArea;
      var off2 = off1 + 4 + wav.Length;

      Write32(audo, 2);
      Write32(audo, (uint)off1);
      Write32(audo, (uint)off2);
      Write32(audo, (uint)wav.Length);
      audo.Write(wav);
      Write32(audo, (uint)ogg.Length);
      audo.Write(ogg);

      AppendChunk(body, "AUDO", audo.ToArray());
    }

    // ── STRG: two short strings ──────────────────────────────────────────────
    using (var strg = new MemoryStream()) {
      Write32(strg, 2);
      Write32(strg, 0); // ptr values unused by descriptor (walks sequentially)
      Write32(strg, 0);

      var s1 = Encoding.UTF8.GetBytes("hello");
      Write32(strg, (uint)s1.Length);
      strg.Write(s1);
      strg.WriteByte(0);

      var s2 = Encoding.UTF8.GetBytes("world");
      Write32(strg, (uint)s2.Length);
      strg.Write(s2);
      strg.WriteByte(0);

      AppendChunk(body, "STRG", strg.ToArray());
    }

    // ── Wrap in FORM ──────────────────────────────────────────────────────────
    using var form = new MemoryStream();
    form.Write("FORM"u8);
    var bodyBytes = body.ToArray();
    Write32(form, (uint)bodyBytes.Length);
    form.Write(bodyBytes);
    return form.ToArray();
  }

  private static byte[] BuildSyntheticPng(byte[] payload) {
    var buf = new byte[PngMagic.Length + payload.Length];
    Array.Copy(PngMagic, 0, buf, 0, PngMagic.Length);
    Array.Copy(payload, 0, buf, PngMagic.Length, payload.Length);
    return buf;
  }

  private static void AppendChunk(Stream dest, string tag, byte[] data) {
    dest.Write(Encoding.ASCII.GetBytes(tag));
    Write32(dest, (uint)data.Length);
    dest.Write(data);
  }

  private static void Write32(Stream dest, uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(b, v);
    dest.Write(b);
  }

  [Test]
  public void List_ReturnsCanonicalEntries() {
    var data = MakeMinimalWin();
    using var ms = new MemoryStream(data);
    var entries = new GameMakerFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.win"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "chunks/GEN8.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name == "chunks/TXTR.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name == "chunks/AUDO.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name == "chunks/STRG.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("textures/", StringComparison.Ordinal)), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("audio/", StringComparison.Ordinal)), Is.True);
    Assert.That(entries.Any(e => e.Name == "strings.txt"), Is.True);
  }

  [Test]
  public void Extract_WritesExpectedFiles() {
    var data = MakeMinimalWin();
    var tmp = Path.Combine(Path.GetTempPath(), "gm_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new GameMakerFormatDescriptor().Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "FULL.win")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "strings.txt")), Is.True);
      Assert.That(Directory.Exists(Path.Combine(tmp, "textures")), Is.True);
      Assert.That(Directory.Exists(Path.Combine(tmp, "audio")), Is.True);
      Assert.That(Directory.Exists(Path.Combine(tmp, "chunks")), Is.True);

      var strings = File.ReadAllText(Path.Combine(tmp, "strings.txt"));
      Assert.That(strings, Does.Contain("hello"));
      Assert.That(strings, Does.Contain("world"));

      // Two PNG textures produced.
      var pngCount = Directory.GetFiles(Path.Combine(tmp, "textures"), "*.png").Length;
      Assert.That(pngCount, Is.EqualTo(2));

      // One WAV + one OGG produced.
      var wavCount = Directory.GetFiles(Path.Combine(tmp, "audio"), "*.wav").Length;
      var oggCount = Directory.GetFiles(Path.Combine(tmp, "audio"), "*.ogg").Length;
      Assert.That(wavCount, Is.EqualTo(1));
      Assert.That(oggCount, Is.EqualTo(1));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void CorruptInput_DoesNotThrow() {
    var junk = new byte[64];
    "FORM"u8.CopyTo(junk.AsSpan());
    BinaryPrimitives.WriteUInt32LittleEndian(junk.AsSpan(4), 0xFFFFFFFFu); // lying size
    using var ms = new MemoryStream(junk);
    Assert.DoesNotThrow(() => new GameMakerFormatDescriptor().List(ms, null));
  }
}
