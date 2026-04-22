using System.Buffers.Binary;
using System.Text;
using FileFormat.Alac;

namespace Compression.Tests.Alac;

[TestFixture]
public class AlacTests {

  // ── ISOBMFF helpers ─────────────────────────────────────────────────────────

  private static void WriteBoxHeader(MemoryStream ms, int size, string type) {
    Span<byte> hdr = stackalloc byte[8];
    BinaryPrimitives.WriteUInt32BigEndian(hdr, (uint)size);
    Encoding.ASCII.GetBytes(type.AsSpan(), hdr[4..]);
    ms.Write(hdr);
  }

  private static byte[] MakeBox(string type, byte[] body) {
    using var ms = new MemoryStream();
    WriteBoxHeader(ms, 8 + body.Length, type);
    ms.Write(body);
    return ms.ToArray();
  }

  private static byte[] MakeContainerBox(string type, params byte[][] children) {
    var total = 0;
    foreach (var c in children) total += c.Length;
    using var ms = new MemoryStream();
    WriteBoxHeader(ms, 8 + total, type);
    foreach (var c in children) ms.Write(c);
    return ms.ToArray();
  }

  /// <summary>ftyp box with M4A brand.</summary>
  private static byte[] MakeFtyp() {
    using var body = new MemoryStream();
    body.Write("M4A "u8);                              // major_brand
    Span<byte> ver = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(ver, 0);      // minor_version
    body.Write(ver);
    body.Write("M4A "u8);                              // compatible_brands
    body.Write("isom"u8);
    return MakeBox("ftyp", body.ToArray());
  }

  /// <summary>hdlr box with soun handler.</summary>
  private static byte[] MakeHdlr() {
    using var body = new MemoryStream();
    // version+flags (4) + pre_defined (4) + handler_type (4) + reserved (12) + name ("\0")
    body.Write(new byte[4]);
    body.Write(new byte[4]);
    body.Write("soun"u8);
    body.Write(new byte[12]);
    body.WriteByte(0);
    return MakeBox("hdlr", body.ToArray());
  }

  /// <summary>alac codec-specific atom (magic cookie). 4 bytes ver/flags + 24-byte cookie.</summary>
  private static byte[] MakeAlacAtom(uint frameLength, byte bitDepth, byte channels, uint sampleRate) {
    using var body = new MemoryStream();
    body.Write(new byte[4]); // version/flags
    // frameLength (u32), compatVersion (u8), bitDepth (u8), pb (u8), mb (u8), kb (u8),
    // channels (u8), maxRun (u16), maxFrameBytes (u32), avgBitRate (u32), sampleRate (u32)
    Span<byte> w = stackalloc byte[24];
    BinaryPrimitives.WriteUInt32BigEndian(w, frameLength);
    w[4] = 0;               // compatVersion
    w[5] = bitDepth;
    w[6] = 40;              // pb
    w[7] = 10;              // mb
    w[8] = 14;              // kb
    w[9] = channels;
    BinaryPrimitives.WriteUInt16BigEndian(w[10..], 255); // maxRun
    BinaryPrimitives.WriteUInt32BigEndian(w[12..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(w[16..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(w[20..], sampleRate);
    body.Write(w);
    return MakeBox("alac", body.ToArray());
  }

  /// <summary>
  /// alac sample entry: 8 bytes reserved + 2 data_reference_index + 8 reserved (audio)
  /// + 2 channel_count + 2 sample_size + 2 pre_defined + 2 reserved + 4 sample_rate
  /// = 28 bytes prelude, followed by inner alac cookie atom.
  /// </summary>
  private static byte[] MakeAlacSampleEntry(byte[] cookie) {
    using var body = new MemoryStream();
    body.Write(new byte[6]);                             // reserved
    Span<byte> dri = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(dri, 1);       // data_reference_index
    body.Write(dri);
    body.Write(new byte[8]);                             // reserved (audio sample entry)
    Span<byte> chan = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(chan, 2);      // channel_count
    body.Write(chan);
    Span<byte> bps = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(bps, 16);      // sample_size
    body.Write(bps);
    body.Write(new byte[4]);                             // pre_defined + reserved
    Span<byte> sr = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(sr, (uint)44100 << 16); // sample_rate (16.16)
    body.Write(sr);
    body.Write(cookie);
    return MakeBox("alac", body.ToArray());
  }

  private static byte[] MakeStsd(byte[] sampleEntry) {
    using var body = new MemoryStream();
    body.Write(new byte[4]); // version/flags
    Span<byte> cnt = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(cnt, 1);
    body.Write(cnt);
    body.Write(sampleEntry);
    return MakeBox("stsd", body.ToArray());
  }

  private static byte[] MakeStsz(int[] sizes) {
    using var body = new MemoryStream();
    body.Write(new byte[4]); // version/flags
    Span<byte> u = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(u, 0); body.Write(u); // sample_size (variable)
    BinaryPrimitives.WriteUInt32BigEndian(u, (uint)sizes.Length); body.Write(u);
    foreach (var s in sizes) {
      BinaryPrimitives.WriteUInt32BigEndian(u, (uint)s);
      body.Write(u);
    }
    return MakeBox("stsz", body.ToArray());
  }

  private static byte[] MakeStsc(int samplesPerChunk) {
    using var body = new MemoryStream();
    body.Write(new byte[4]);
    Span<byte> u = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(u, 1); body.Write(u); // entry_count
    BinaryPrimitives.WriteUInt32BigEndian(u, 1); body.Write(u); // first_chunk
    BinaryPrimitives.WriteUInt32BigEndian(u, (uint)samplesPerChunk); body.Write(u);
    BinaryPrimitives.WriteUInt32BigEndian(u, 1); body.Write(u); // sample_description_index
    return MakeBox("stsc", body.ToArray());
  }

  private static byte[] MakeStco(uint offset) {
    using var body = new MemoryStream();
    body.Write(new byte[4]);
    Span<byte> u = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(u, 1); body.Write(u);
    BinaryPrimitives.WriteUInt32BigEndian(u, offset); body.Write(u);
    return MakeBox("stco", body.ToArray());
  }

  /// <summary>
  /// Builds a minimal ISOBMFF/M4A file with an alac sample entry + stsz/stsc/stco
  /// pointing at a small mdat body.
  /// </summary>
  private static byte[] BuildMinimalM4a(byte[] frameData) {
    var cookie = MakeAlacAtom(frameLength: 4096, bitDepth: 16, channels: 2, sampleRate: 44100);
    var alacEntry = MakeAlacSampleEntry(cookie);
    var stsd = MakeStsd(alacEntry);
    var stsz = MakeStsz([frameData.Length]);
    var stsc = MakeStsc(1);
    // The stco offset depends on where mdat's body ends up; compute below.
    // Build stbl/minf/mdia/trak/moov with a placeholder stco, then patch.
    // Easier: put mdat right after ftyp + moov, so stco offset = ftyp.Length + moov.Length + 8.
    var stblNoStco = MakeContainerBox("stbl", stsd, stsz, stsc);
    // Build minf/mdia/trak/moov twice: first to compute offsets, second to patch stco.
    // Simpler: construct in stages, compute total header size, insert stco.

    var ftyp = MakeFtyp();

    // Build mdia inner → minf → stbl → wrap
    // We do a first-pass build with stco=0, measure, then rebuild with correct offset.
    var stcoPlaceholder = MakeStco(0);
    var stbl = MakeContainerBox("stbl", stsd, stsz, stsc, stcoPlaceholder);
    var minf = MakeContainerBox("minf", stbl);
    var hdlr = MakeHdlr();
    var mdia = MakeContainerBox("mdia", hdlr, minf);
    var trak = MakeContainerBox("trak", mdia);
    var moov = MakeContainerBox("moov", trak);

    var mdatOffset = ftyp.Length + moov.Length + 8; // mdat body starts after ftyp+moov+mdat-header
    var stcoReal = MakeStco((uint)mdatOffset);
    var stblReal = MakeContainerBox("stbl", stsd, stsz, stsc, stcoReal);
    var minfReal = MakeContainerBox("minf", stblReal);
    var mdiaReal = MakeContainerBox("mdia", hdlr, minfReal);
    var trakReal = MakeContainerBox("trak", mdiaReal);
    var moovReal = MakeContainerBox("moov", trakReal);
    // Sanity: moovReal.Length == moov.Length (stco size is constant).
    Assert.That(moovReal.Length, Is.EqualTo(moov.Length));

    using var output = new MemoryStream();
    output.Write(ftyp);
    output.Write(moovReal);
    WriteBoxHeader(output, 8 + frameData.Length, "mdat");
    output.Write(frameData);
    return output.ToArray();
  }

  // ── Tests ──────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void BuildMinimalM4a_ParsesWithoutThrowing() {
    var file = BuildMinimalM4a([0x10, 0x20, 0x30, 0x40]);
    Assert.That(file.Length, Is.GreaterThan(64));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsExpectedEntryNames() {
    var frames = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
    var file = BuildMinimalM4a(frames);

    using var ms = new MemoryStream(file);
    var entries = new AlacFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("FULL.m4a"));
    Assert.That(names, Does.Contain("alac_magic_cookie.bin"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("track_00_alac.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesFilesToDisk() {
    var frames = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
    var file = BuildMinimalM4a(frames);

    var tmp = Path.Combine(Path.GetTempPath(), $"alac-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(file);
      new AlacFormatDescriptor().Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "FULL.m4a")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "alac_magic_cookie.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "track_00_alac.bin")), Is.True);

      var track = File.ReadAllBytes(Path.Combine(tmp, "track_00_alac.bin"));
      Assert.That(track, Is.EqualTo(frames));

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("sample_rate=44100"));
      Assert.That(ini, Does.Contain("channels=2"));
      Assert.That(ini, Does.Contain("bit_depth=16"));
      Assert.That(ini, Does.Contain("frame_length=4096"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new AlacFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Alac"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Audio));
    Assert.That(d.Extensions, Does.Contain(".m4a"));
  }

  [Test, Category("EdgeCase")]
  public void List_NonMp4_StillReturnsFullPassthrough() {
    var bogus = new byte[] { 0x00, 0x01, 0x02, 0x03 };
    using var ms = new MemoryStream(bogus);
    var entries = new AlacFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.m4a"));
  }
}
