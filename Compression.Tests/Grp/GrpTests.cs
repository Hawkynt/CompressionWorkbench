namespace Compression.Tests.Grp;

[TestFixture]
public class GrpTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello World"u8.ToArray();
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Grp.GrpWriter(archive, leaveOpen: true)) {
      w.AddFile("test.txt", data);
      w.Finish();
    }
    archive.Position = 0;
    var r = new FileFormat.Grp.GrpReader(archive);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = "File one content"u8.ToArray();
    var data2 = "File two content"u8.ToArray();
    var data3 = "File three!"u8.ToArray();
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Grp.GrpWriter(archive, leaveOpen: true)) {
      w.AddFile("alpha.dat", data1);
      w.AddFile("beta.dat", data2);
      w.AddFile("gamma.dat", data3);
      w.Finish();
    }
    archive.Position = 0;
    var r = new FileFormat.Grp.GrpReader(archive);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(data3));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Grp.GrpFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Grp"));
    Assert.That(d.Extensions, Contains.Item(".grp"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes,
      Is.EqualTo(System.Text.Encoding.ASCII.GetBytes("KenSilverman")));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("EdgeCase")]
  public void BadMagic_Throws() {
    var buf = new byte[16];
    Array.Fill(buf, (byte)0xCC);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Grp.GrpReader(ms));
  }

  [Test, Category("EdgeCase")]
  public void TooSmall_Throws() {
    var buf = new byte[4];
    Array.Fill(buf, (byte)0xAA);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Grp.GrpReader(ms));
  }

  [Test, Category("EdgeCase")]
  public void LongFilename_Truncated() {
    var data = "payload"u8.ToArray();
    // Name longer than 12 characters — writer must truncate
    const string longName = "VeryLongFilename.dat"; // 20 chars
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Grp.GrpWriter(archive, leaveOpen: true)) {
      w.AddFile(longName, data);
      w.Finish();
    }
    archive.Position = 0;
    var r = new FileFormat.Grp.GrpReader(archive);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name.Length, Is.LessThanOrEqualTo(12));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }
}
