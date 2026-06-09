using System.Text;
using Compression.Registry;
using FileFormat.Rpa;

namespace Compression.Tests.Rpa;

[TestFixture]
public class RpaWriterTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var payload = "the quick brown fox jumps over the lazy dog"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new RpaWriter(ms, leaveOpen: true))
      w.AddEntry("fox.txt", payload);
    ms.Position = 0;

    var r = new RpaReader(ms);
    Assert.That(r.Version, Is.EqualTo("RPA-3.0"));
    Assert.That(r.PickleParsed, Is.True);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Path, Is.EqualTo("fox.txt"));
    Assert.That(r.Entries[0].Length, Is.EqualTo(payload.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var files = new (string Name, byte[] Data)[] {
      ("a.txt", Encoding.UTF8.GetBytes("Alpha")),
      ("subdir/b.bin", new byte[] { 1, 2, 3, 4, 5 }),
      ("game/script.rpyc", Encoding.UTF8.GetBytes(new string('Z', 256))),
    };

    using var ms = new MemoryStream();
    using (var w = new RpaWriter(ms, leaveOpen: true))
      foreach (var (name, data) in files)
        w.AddEntry(name, data);
    ms.Position = 0;

    var r = new RpaReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));

    foreach (var (name, data) in files) {
      var e = r.Entries.Single(x => x.Path == name);
      Assert.That(r.Extract(e), Is.EqualTo(data), $"Entry {name} should round-trip exactly");
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_CustomXorKey() {
    var payload = "secret"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new RpaWriter(ms, leaveOpen: true, xorKey: 0xCAFEBABEu))
      w.AddEntry("secret.txt", payload);
    ms.Position = 0;

    var r = new RpaReader(ms);
    Assert.That(r.XorKey, Is.EqualTo(0xCAFEBABEu));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_WithPrefix() {
    // Prefix carries the first N bytes inside the pickle; the body lives in the payload region.
    var prefix = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
    var body = Encoding.UTF8.GetBytes("body");
    var full = prefix.Concat(body).ToArray();

    using var ms = new MemoryStream();
    using (var w = new RpaWriter(ms, leaveOpen: true))
      w.AddEntry("prefixed.bin", full, prefix);
    ms.Position = 0;

    var r = new RpaReader(ms);
    Assert.That(r.Entries[0].Prefix, Is.EqualTo(prefix));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(full));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughExtract() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("hello.txt", "world!"u8.ToArray()),
      ArchiveInputInfo.InMemory("data/foo.bin", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
    };

    using var ms = new MemoryStream();
    var d = new RpaFormatDescriptor();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var list = d.List(ms, null);
    Assert.That(list.Any(e => e.Name == "FULL.rpa"), Is.True);
    Assert.That(list.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(list.Any(e => e.Name == "hello.txt"), Is.True);
    Assert.That(list.Any(e => e.Name == "data/foo.bin"), Is.True);

    ms.Position = 0;
    var bytes = d.ExtractEntryToMemory(ms, "hello.txt", null);
    Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo("world!"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate() {
    var d = new RpaFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("EdgeCase")]
  public void HeaderHasCorrectShape() {
    using var ms = new MemoryStream();
    using (var w = new RpaWriter(ms, leaveOpen: true))
      w.AddEntry("a.txt", "hello"u8.ToArray());

    var text = Encoding.ASCII.GetString(ms.ToArray(), 0, 34);
    Assert.That(text, Does.StartWith("RPA-3.0 "));
    Assert.That(text, Does.EndWith("\n"));
    // Layout: "RPA-3.0 <16hex> <8hex>\n"  → 8 + 16 + 1 + 8 + 1 = 34 chars.
    var parts = text.TrimEnd('\n').Split(' ');
    Assert.That(parts, Has.Length.EqualTo(3));
    Assert.That(parts[1], Has.Length.EqualTo(16));
    Assert.That(parts[2], Has.Length.EqualTo(8));
  }

  [Test, Category("EdgeCase")]
  public void EmptyArchive_StillRoundTrips() {
    using var ms = new MemoryStream();
    using (var _ = new RpaWriter(ms, leaveOpen: true)) { /* no entries */ }
    ms.Position = 0;

    var r = new RpaReader(ms);
    Assert.That(r.Version, Is.EqualTo("RPA-3.0"));
    Assert.That(r.Entries, Is.Empty);
  }
}
