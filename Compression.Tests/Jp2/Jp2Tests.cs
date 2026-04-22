#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.Jp2;

namespace Compression.Tests.Jp2;

[TestFixture]
public class Jp2Tests {

  /// <summary>
  /// Builds a minimal JP2 box-form fixture: signature box, ftyp, jp2h/ihdr,
  /// one jp2c codestream containing SOC+SIZ+SOT+EOC, plus a small xml box.
  /// </summary>
  private static byte[] MakeMinimalJp2Box() {
    using var ms = new MemoryStream();

    // Signature box: size(4 BE) + 'jP  ' + 0x0D 0x0A 0x87 0x0A
    WriteBox(ms, "jP  ", new byte[] { 0x0D, 0x0A, 0x87, 0x0A });

    // File Type box (ftyp): major brand 'jp2 ', minor version, compat brands 'jp2 '.
    var ftyp = new List<byte>();
    ftyp.AddRange("jp2 "u8.ToArray());
    ftyp.AddRange(new byte[] { 0, 0, 0, 0 });
    ftyp.AddRange("jp2 "u8.ToArray());
    WriteBox(ms, "ftyp", ftyp.ToArray());

    // JP2 Header box (jp2h), containing just ihdr (14 bytes).
    // ihdr: height(4 BE), width(4 BE), nc(2 BE), bpc(1), c(1), unk(1), ipr(1)
    var ihdr = new byte[14];
    BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), 32u);   // height
    BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), 48u);   // width
    BinaryPrimitives.WriteUInt16BigEndian(ihdr.AsSpan(8, 2), 3);     // nc
    ihdr[10] = 7;                                                     // bpc: 7+1=8
    ihdr[11] = 7; ihdr[12] = 0; ihdr[13] = 0;
    using var jp2hBody = new MemoryStream();
    WriteBox(jp2hBody, "ihdr", ihdr);
    WriteBox(ms, "jp2h", jp2hBody.ToArray());

    // XML box with a tiny payload.
    WriteBox(ms, "xml ", "<x/>"u8.ToArray());

    // UUID box with 16-byte UUID prefix (typical of EXIF UUID).
    var uuidBody = new byte[16 + 4];
    for (var i = 0; i < 16; i++) uuidBody[i] = (byte)i;
    uuidBody[16] = 0xDE; uuidBody[17] = 0xAD; uuidBody[18] = 0xBE; uuidBody[19] = 0xEF;
    WriteBox(ms, "uuid", uuidBody);

    // Codestream box (jp2c): SOC + SIZ header + SOT + EOC.
    using var cs = new MemoryStream();
    cs.Write(new byte[] { 0xFF, 0x4F });                            // SOC
    // SIZ: FF 51 Lsiz Rsiz Xsiz Ysiz XO YO XT YT XTO YTO Csiz [per-comp 3]
    cs.Write(new byte[] { 0xFF, 0x51 });
    // Lsiz = 38 + 3*Csiz for Csiz=3 => Lsiz = 47
    var siz = new byte[45];
    BinaryPrimitives.WriteUInt16BigEndian(siz.AsSpan(0, 2), 47);      // Lsiz
    BinaryPrimitives.WriteUInt16BigEndian(siz.AsSpan(2, 2), 0);       // Rsiz
    BinaryPrimitives.WriteUInt32BigEndian(siz.AsSpan(4, 4), 48);      // Xsiz
    BinaryPrimitives.WriteUInt32BigEndian(siz.AsSpan(8, 4), 32);      // Ysiz
    // XOsiz..YTOsiz zero (24 bytes already zero)
    BinaryPrimitives.WriteUInt16BigEndian(siz.AsSpan(36, 2), 3);      // Csiz
    siz[38] = 7; siz[39] = 1; siz[40] = 1;                             // comp 0
    siz[41] = 7; siz[42] = 1; siz[43] = 1;                             // comp 1
    siz[44] = 7;                                                       // comp 2 Ssiz (partial — we'll add last two via cs.Write below)
    cs.Write(siz);
    cs.Write(new byte[] { 0x01, 0x01 });                               // comp 2 XRsiz YRsiz
    // SOT marker
    cs.Write(new byte[] { 0xFF, 0x90, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00 });
    // A dummy EOC
    cs.Write(new byte[] { 0xFF, 0xD9 });
    WriteBox(ms, "jp2c", cs.ToArray());

    return ms.ToArray();
  }

  private static void WriteBox(Stream ms, string type, byte[] body) {
    Span<byte> header = stackalloc byte[8];
    BinaryPrimitives.WriteUInt32BigEndian(header[..4], (uint)(8 + body.Length));
    header[4] = (byte)type[0]; header[5] = (byte)type[1]; header[6] = (byte)type[2]; header[7] = (byte)type[3];
    ms.Write(header);
    ms.Write(body);
  }

  private static byte[] MakeRawCodestream() {
    using var cs = new MemoryStream();
    cs.Write(new byte[] { 0xFF, 0x4F });
    cs.Write(new byte[] { 0xFF, 0x51 });
    var siz = new byte[45];
    BinaryPrimitives.WriteUInt16BigEndian(siz.AsSpan(0, 2), 47);
    BinaryPrimitives.WriteUInt16BigEndian(siz.AsSpan(2, 2), 0);
    BinaryPrimitives.WriteUInt32BigEndian(siz.AsSpan(4, 4), 16);
    BinaryPrimitives.WriteUInt32BigEndian(siz.AsSpan(8, 4), 8);
    BinaryPrimitives.WriteUInt16BigEndian(siz.AsSpan(36, 2), 1);
    siz[38] = 7; siz[39] = 1; siz[40] = 1;
    siz[41] = 0; siz[42] = 0; siz[43] = 0; siz[44] = 0;
    cs.Write(siz);
    cs.Write(new byte[] { 0x00, 0x00 });
    cs.Write(new byte[] { 0xFF, 0x90, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00 });
    cs.Write(new byte[] { 0xFF, 0xD9 });
    return cs.ToArray();
  }

  [Test]
  public void BoxForm_ListsFullCodestreamAndTile() {
    var data = MakeMinimalJp2Box();
    using var ms = new MemoryStream(data);
    var entries = new Jp2FormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.jp2"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "codestream.j2c"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata/xml_00.xml"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata/uuid_00.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name == "images/tile_00.j2c"), Is.True);
  }

  [Test]
  public void BoxForm_MetadataIniContainsDimensions() {
    var data = MakeMinimalJp2Box();
    var tmp = Path.Combine(Path.GetTempPath(), "jp2_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new Jp2FormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.jp2")), Is.True);
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("form=box"));
      Assert.That(ini, Does.Contain("width=48"));
      Assert.That(ini, Does.Contain("height=32"));
      Assert.That(ini, Does.Contain("num_components=3"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void Codestream_ListsFullAndTile() {
    var data = MakeRawCodestream();
    using var ms = new MemoryStream(data);
    var entries = new Jp2FormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.j2c"), Is.True);
    var meta = entries.First(e => e.Name == "metadata.ini");
    Assert.That(meta, Is.Not.Null);
    Assert.That(entries.Any(e => e.Name == "images/tile_00.j2c"), Is.True);
  }

  [Test]
  public void Codestream_MetadataIniMarksForm() {
    var data = MakeRawCodestream();
    var tmp = Path.Combine(Path.GetTempPath(), "j2c_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new Jp2FormatDescriptor().Extract(ms, tmp, null, null);
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("form=codestream"));
      Assert.That(ini, Does.Contain("width=16"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }
}
