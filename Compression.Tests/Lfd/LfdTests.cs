namespace Compression.Tests.Lfd;

[TestFixture]
public class LfdTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleResource() {
    var body = "starfield bitmap"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Lfd.LfdWriter(ms, leaveOpen: true))
      w.AddEntry("BMAP", "STARS", body);
    ms.Position = 0;

    var r = new FileFormat.Lfd.LfdReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));

    var rmap = r.Entries[0];
    Assert.That(rmap.Type, Is.EqualTo("RMAP"));

    var bmap = r.Entries[1];
    Assert.That(bmap.Type, Is.EqualTo("BMAP"));
    Assert.That(bmap.Name, Is.EqualTo("STARS"));
    Assert.That(bmap.DisplayName, Is.EqualTo("BMAP.STARS"));
    Assert.That(r.Extract(bmap), Is.EqualTo(body));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleResources() {
    var resources = new (string Type, string Name, byte[] Data)[] {
      ("BMAP", "STARS", [1, 2, 3, 4]),
      ("DELT", "SHIP01", [5, 6, 7]),
      ("VOIC", "EXPLODE", "boom!"u8.ToArray()),
      ("FONT", "MAIN", new byte[64]),
    };

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Lfd.LfdWriter(ms, leaveOpen: true)) {
      foreach (var (t, n, d) in resources)
        w.AddEntry(t, n, d);
    }
    ms.Position = 0;

    var r = new FileFormat.Lfd.LfdReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(resources.Length + 1));

    Assert.That(r.Entries[0].Type, Is.EqualTo("RMAP"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(resources.Length * 16));

    // RMAP payload must describe each user resource (type, name, size) in order.
    var rmapPayload = r.Extract(r.Entries[0]);
    Assert.That(rmapPayload, Has.Length.EqualTo(resources.Length * 16));
    for (var i = 0; i < resources.Length; ++i) {
      var slice = rmapPayload.AsSpan(i * 16, 16);
      var typeBytes = slice[..4].ToArray();
      var nameBytes = slice.Slice(4, 8).ToArray();
      var size = BitConverter.ToUInt32(slice[12..]);

      var type = System.Text.Encoding.ASCII.GetString(typeBytes).TrimEnd('\0');
      var name = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');

      Assert.That(type, Is.EqualTo(resources[i].Type));
      Assert.That(name, Is.EqualTo(resources[i].Name));
      Assert.That(size, Is.EqualTo((uint)resources[i].Data.Length));
    }

    // And the actual entries follow in order with correct payloads.
    for (var i = 0; i < resources.Length; ++i) {
      var entry = r.Entries[i + 1];
      Assert.That(entry.Type, Is.EqualTo(resources[i].Type));
      Assert.That(entry.Name, Is.EqualTo(resources[i].Name));
      Assert.That(entry.DisplayName, Is.EqualTo(resources[i].Type + "." + resources[i].Name));
      Assert.That(r.Extract(entry), Is.EqualTo(resources[i].Data));
    }
  }

  [Test, Category("HappyPath")]
  public void Reader_ValidatesRMAP() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Lfd.LfdWriter(ms, leaveOpen: true)) {
      w.AddEntry("BMAP", "A", [1]);
      w.AddEntry("BMAP", "B", [2, 3]);
      w.AddEntry("BMAP", "C", [4, 5, 6]);
    }
    ms.Position = 0;

    var r = new FileFormat.Lfd.LfdReader(ms);
    Assert.That(r.Entries[0].Type, Is.EqualTo("RMAP"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(3 * 16));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsTruncated() {
    using var ms = new MemoryStream(new byte[12]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Lfd.LfdReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsImpossibleSize() {
    // 16-byte header claiming a 1 MB payload, with no payload bytes following.
    var buf = new byte[16];
    System.Text.Encoding.ASCII.GetBytes("BMAP").CopyTo(buf, 0);
    System.Text.Encoding.ASCII.GetBytes("HUGE").CopyTo(buf, 4);
    BitConverter.GetBytes(1_000_000u).CopyTo(buf, 12);

    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Lfd.LfdReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Writer_RejectsLongType() {
    using var ms = new MemoryStream();
    using var w = new FileFormat.Lfd.LfdWriter(ms, leaveOpen: true);
    Assert.Throws<ArgumentException>(() => w.AddEntry("RMAPS", "X", [0]));
  }

  [Test, Category("ErrorHandling")]
  public void Writer_RejectsLongName() {
    using var ms = new MemoryStream();
    using var w = new FileFormat.Lfd.LfdWriter(ms, leaveOpen: true);
    Assert.Throws<ArgumentException>(() => w.AddEntry("BMAP", "TOOLONGNAME", [0]));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Lfd.LfdFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Lfd"));
    Assert.That(d.Extensions, Contains.Item(".lfd"));
    Assert.That(d.MagicSignatures, Is.Empty);
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".lfd"));
    Assert.That(d.Methods[0].Name, Is.EqualTo("lfd"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("LFD"));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
  }
}
