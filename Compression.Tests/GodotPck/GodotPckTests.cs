using FileFormat.GodotPck;

namespace Compression.Tests.GodotPck;

[TestFixture]
public class GodotPckTests {

  // ── helpers ────────────────────────────────────────────────────────────────

  private static MemoryStream BuildPck(Action<PckWriter> populate) {
    var ms = new MemoryStream();
    using (var w = new PckWriter(ms, leaveOpen: true))
      populate(w);
    ms.Position = 0;
    return ms;
  }

  // ── round-trip tests ───────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello Godot!"u8.ToArray();

    using var ms = BuildPck(w => w.AddFile("res://hello.txt", data));
    var r = new PckReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Path, Is.EqualTo("res://hello.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var file1 = "First file content"u8.ToArray();
    var file2 = "Second file content"u8.ToArray();
    var file3 = new byte[512];
    Array.Fill(file3, (byte)0xAB);

    using var ms = BuildPck(w => {
      w.AddFile("res://a.txt", file1);
      w.AddFile("res://b.txt", file2);
      w.AddFile("res://c.bin", file3);
    });

    var r = new PckReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(file1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(file2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(file3));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_NestedPaths() {
    var sceneData  = "<scene />"u8.ToArray();
    var scriptData = "extends Node"u8.ToArray();
    var texData    = new byte[256];
    Array.Fill(texData, (byte)0xFF);

    using var ms = BuildPck(w => {
      w.AddFile("res://scenes/main.tscn", sceneData);
      w.AddFile("res://scripts/player.gd", scriptData);
      w.AddFile("res://assets/icon.png", texData);
    });

    var r = new PckReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Path, Is.EqualTo("res://scenes/main.tscn"));
    Assert.That(r.Entries[1].Path, Is.EqualTo("res://scripts/player.gd"));
    Assert.That(r.Entries[2].Path, Is.EqualTo("res://assets/icon.png"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(sceneData));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(scriptData));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(texData));
  }

  // ── descriptor / property tests ────────────────────────────────────────────

  [Test]
  public void Descriptor_Properties() {
    var d = new GodotPckFormatDescriptor();

    Assert.That(d.Id, Is.EqualTo("GodotPck"));
    Assert.That(d.DisplayName, Is.EqualTo("Godot PCK"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".pck"));
    Assert.That(d.Extensions, Contains.Item(".pck"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("GDPC"u8.ToArray()));
    Assert.That(d.Capabilities & Compression.Registry.FormatCapabilities.CanList,    Is.Not.EqualTo(0));
    Assert.That(d.Capabilities & Compression.Registry.FormatCapabilities.CanExtract, Is.Not.EqualTo(0));
    Assert.That(d.Capabilities & Compression.Registry.FormatCapabilities.CanCreate,  Is.Not.EqualTo(0));
    Assert.That(d.Capabilities & Compression.Registry.FormatCapabilities.CanTest,    Is.Not.EqualTo(0));
    Assert.That(d.Capabilities & Compression.Registry.FormatCapabilities.SupportsMultipleEntries, Is.Not.EqualTo(0));
  }

  [Test]
  public void Descriptor_List_ViaInterface() {
    var data = "via interface"u8.ToArray();
    using var ms = BuildPck(w => w.AddFile("res://test.txt", data));

    var d = new GodotPckFormatDescriptor();
    var entries = d.List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("res://test.txt"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(data.Length));
  }

  // ── error-path tests ───────────────────────────────────────────────────────

  [Test]
  public void BadMagic_Throws() {
    // 40+ bytes but wrong magic
    var buf = new byte[64];
    Array.Fill(buf, (byte)0x00);
    buf[0] = (byte)'Z'; buf[1] = (byte)'I'; buf[2] = (byte)'P'; buf[3] = (byte)' ';
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new PckReader(ms));
  }

  [Test]
  public void TooSmall_Throws() {
    // Only 10 bytes — well below the minimum header size
    var buf = new byte[10];
    Array.Fill(buf, (byte)0x47); // 'G'
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new PckReader(ms));
  }
}
