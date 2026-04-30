using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Mhk;

[TestFixture]
public class MhkTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleResource() {
    var body = "tBMP raw payload"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Mhk.MhkWriter(ms, leaveOpen: true))
      w.AddEntry("tBMP", 1000, null, body);
    ms.Position = 0;

    var r = new FileFormat.Mhk.MhkReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));

    var e = r.Entries[0];
    Assert.That(e.Type, Is.EqualTo("tBMP"));
    Assert.That(e.Id, Is.EqualTo((ushort)1000));
    Assert.That(e.Name, Is.Null);
    Assert.That(e.Size, Is.EqualTo(body.Length));
    Assert.That(e.DisplayName, Is.EqualTo("tBMP_1000"));
    Assert.That(r.Extract(e), Is.EqualTo(body));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_NamedResource() {
    var body = "wave samples"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Mhk.MhkWriter(ms, leaveOpen: true))
      w.AddEntry("tWAV", 50, "intro_music", body);
    ms.Position = 0;

    var r = new FileFormat.Mhk.MhkReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));

    var e = r.Entries[0];
    Assert.That(e.Type, Is.EqualTo("tWAV"));
    Assert.That(e.Id, Is.EqualTo((ushort)50));
    Assert.That(e.Name, Is.EqualTo("intro_music"));
    Assert.That(e.DisplayName, Is.EqualTo("tWAV_50_intro_music"));
    Assert.That(r.Extract(e), Is.EqualTo(body));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleTypes() {
    var bmp1 = new byte[200];
    var bmp2 = new byte[150];
    var wav  = new byte[300];
    Array.Fill(bmp1, (byte)0x11);
    Array.Fill(bmp2, (byte)0x22);
    Array.Fill(wav,  (byte)0x33);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Mhk.MhkWriter(ms, leaveOpen: true)) {
      w.AddEntry("tBMP", 1, null, bmp1);
      w.AddEntry("tBMP", 2, "second_bitmap", bmp2);
      w.AddEntry("tWAV", 100, null, wav);
    }
    ms.Position = 0;

    var r = new FileFormat.Mhk.MhkReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));

    var byKey = r.Entries.ToDictionary(e => $"{e.Type}/{e.Id}");
    Assert.That(byKey["tBMP/1"].Name, Is.Null);
    Assert.That(r.Extract(byKey["tBMP/1"]), Is.EqualTo(bmp1));
    Assert.That(byKey["tBMP/2"].Name, Is.EqualTo("second_bitmap"));
    Assert.That(r.Extract(byKey["tBMP/2"]), Is.EqualTo(bmp2));
    Assert.That(byKey["tWAV/100"].Name, Is.Null);
    Assert.That(r.Extract(byKey["tWAV/100"]), Is.EqualTo(wav));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    // 64 KB exercises the 24-bit low-part of the file size encoding cleanly.
    var body = new byte[64 * 1024];
    for (var i = 0; i < body.Length; ++i)
      body[i] = (byte)(i & 0xFF);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Mhk.MhkWriter(ms, leaveOpen: true))
      w.AddEntry("tBMP", 9999, null, body);
    ms.Position = 0;

    var r = new FileFormat.Mhk.MhkReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(body.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(body));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadOuterMagic() {
    var buf = new byte[64];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Mhk.MhkReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadRsrcMagic() {
    // Hand-craft an outer "MHWK" + 4-byte size, then "XXXX" instead of "RSRC".
    var buf = new byte[64];
    Encoding.ASCII.GetBytes("MHWK").CopyTo(buf, 0);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), (uint)(buf.Length - 8));
    Encoding.ASCII.GetBytes("XXXX").CopyTo(buf, 8);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Mhk.MhkReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Writer_RejectsBadType() {
    using var ms = new MemoryStream();
    using var w = new FileFormat.Mhk.MhkWriter(ms, leaveOpen: true);
    Assert.Throws<ArgumentException>(() => w.AddEntry("ABC", 1, null, [0]));
    Assert.Throws<ArgumentException>(() => w.AddEntry("ABCDE", 1, null, [0]));
  }

  [Test, Category("HappyPath")]
  public void Magic_OuterIsMHWK() {
    var d = new FileFormat.Mhk.MhkFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x4D, 0x48, 0x57, 0x4B }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Mhk.MhkFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Mhk"));
    Assert.That(d.DisplayName, Is.EqualTo("Cyan Mohawk"));
    Assert.That(d.Extensions, Contains.Item(".mhk"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".mhk"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.Methods[0].Name, Is.EqualTo("mhk"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("Mohawk"));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
  }
}
