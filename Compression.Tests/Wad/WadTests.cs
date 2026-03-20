namespace Compression.Tests.Wad;

[TestFixture]
public class WadTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleLump() {
    var data = "DOOM level data"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Wad.WadWriter(ms, leaveOpen: true))
      w.AddLump("TESTLUMP", data);
    ms.Position = 0;

    var r = new FileFormat.Wad.WadReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("TESTLUMP"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleLumps() {
    var data1 = new byte[1024];
    var data2 = new byte[512];
    Random.Shared.NextBytes(data1);
    Random.Shared.NextBytes(data2);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Wad.WadWriter(ms, leaveOpen: true)) {
      w.AddLump("LUMP1", data1);
      w.AddLump("LUMP2", data2);
    }
    ms.Position = 0;

    var r = new FileFormat.Wad.WadReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath")]
  public void Default_IsPwad() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Wad.WadWriter(ms, leaveOpen: true))
      w.AddLump("TEST", [1]);
    ms.Position = 0;

    var r = new FileFormat.Wad.WadReader(ms);
    Assert.That(r.IsPwad, Is.True);
    Assert.That(r.IsIwad, Is.False);
  }

  [Test, Category("HappyPath")]
  public void Iwad_Magic() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Wad.WadWriter(ms, leaveOpen: true, isIwad: true))
      w.AddLump("TEST", [1]);
    ms.Position = 0;

    var r = new FileFormat.Wad.WadReader(ms);
    Assert.That(r.IsIwad, Is.True);
    Assert.That(r.IsPwad, Is.False);
  }

  [Test, Category("HappyPath")]
  public void MarkerLumps_HaveZeroSize() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Wad.WadWriter(ms, leaveOpen: true)) {
      w.AddMarker("S_START");
      w.AddLump("SPRITE1", new byte[64]);
      w.AddMarker("S_END");
    }
    ms.Position = 0;

    var r = new FileFormat.Wad.WadReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("S_START"));
    Assert.That(r.Entries[0].IsMarker, Is.True);
    Assert.That(r.Entries[0].Size, Is.EqualTo(0));
    Assert.That(r.Entries[2].Name, Is.EqualTo("S_END"));
    Assert.That(r.Entries[2].IsMarker, Is.True);
  }
}
