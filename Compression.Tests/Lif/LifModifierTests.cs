#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Lif;

namespace Compression.Tests.Lif;

[TestFixture]
public class LifModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    LifModifier.AddFile(ms, "GREET", "hello-lif"u8.ToArray());

    var v = LifReader.Read(ms.ToArray());
    var entry = v.Files.Single(e => e.Name == "GREET");
    var data = LifReader.Extract(v, entry);
    Assert.That(Encoding.ASCII.GetString(data, 0, 9), Is.EqualTo("hello-lif"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_SpansMultipleSectors() {
    var ms = BuildEmptyImage();
    var data = new byte[2000]; // 8 sectors
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 11) & 0xFF);
    LifModifier.AddFile(ms, "BIG", data);

    var v = LifReader.Read(ms.ToArray());
    var entry = v.Files.Single(e => e.Name == "BIG");
    Assert.That(entry.LengthSectors, Is.EqualTo(8));
    var extracted = LifReader.Extract(v, entry);
    Assert.That(extracted.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void AddMultipleFiles_AllReadable() {
    var ms = BuildEmptyImage();
    LifModifier.AddFile(ms, "ONE", "first"u8.ToArray());
    LifModifier.AddFile(ms, "TWO", "second"u8.ToArray());
    LifModifier.AddFile(ms, "THREE", "third"u8.ToArray());

    var v = LifReader.Read(ms.ToArray());
    Assert.That(v.Files.Select(f => f.Name), Is.EquivalentTo(new[] { "ONE", "TWO", "THREE" }));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesAndAllowsReuse() {
    var ms = BuildEmptyImage();
    LifModifier.AddFile(ms, "OLD", new byte[1000]);
    Assert.That(LifModifier.RemoveFile(ms, "OLD"), Is.True);
    LifModifier.AddFile(ms, "NEW", new byte[1000]);

    var v = LifReader.Read(ms.ToArray());
    Assert.That(v.Files.Any(e => e.Name == "OLD"), Is.False);
    Assert.That(v.Files.Any(e => e.Name == "NEW"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(LifModifier.RemoveFile(ms, "GHOST"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    LifModifier.AddFile(ms, "SECRET", "TOPSECRET-MARKER-LIF"u8.ToArray());
    LifModifier.RemoveFile(ms, "SECRET");
    var asAscii = Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-LIF"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_GapReusedByNextAdd() {
    var ms = BuildEmptyImage();
    LifModifier.AddFile(ms, "A", new byte[300]);   // 2 sectors
    LifModifier.AddFile(ms, "B", new byte[300]);   // 2 sectors
    LifModifier.AddFile(ms, "C", new byte[300]);   // 2 sectors
    var sizeBefore = ms.Length;
    LifModifier.RemoveFile(ms, "B");
    LifModifier.AddFile(ms, "D", new byte[300]);   // should reuse B's gap
    Assert.That(ms.Length, Is.EqualTo(sizeBefore), "Adding a same-size file into a freed gap must not grow the image.");

    var v = LifReader.Read(ms.ToArray());
    Assert.That(v.Files.Select(f => f.Name), Is.EquivalentTo(new[] { "A", "C", "D" }));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new LifFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "VIAIF", false)]);

      var v = LifReader.Read(ms.ToArray());
      Assert.That(v.Files.Any(e => e.Name == "VIAIF"), Is.True);
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_DropsEntry() {
    var ms = BuildEmptyImage();
    LifModifier.AddFile(ms, "DROP", "x"u8.ToArray());
    ((IArchiveModifiable)new LifFormatDescriptor()).Remove(ms, ["DROP"]);

    var v = LifReader.Read(ms.ToArray());
    Assert.That(v.Files.Any(e => e.Name == "DROP"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_ReplacesByName() {
    var ms = BuildEmptyImage();
    LifModifier.AddFile(ms, "DUP", "first-version"u8.ToArray());
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "second-version"u8.ToArray());
      ((IArchiveModifiable)new LifFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "DUP", false)]);

      var v = LifReader.Read(ms.ToArray());
      var matches = v.Files.Where(e => e.Name == "DUP").ToList();
      Assert.That(matches, Has.Count.EqualTo(1), "Replace-by-name must keep a single entry.");
      var data = LifReader.Extract(v, matches[0]);
      Assert.That(Encoding.ASCII.GetString(data, 0, "second-version".Length), Is.EqualTo("second-version"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("EdgeCase")]
  public void Capabilities_AdvertisesCanModify() {
    var caps = new LifFormatDescriptor().Capabilities;
    Assert.That((caps & FormatCapabilities.CanModify) != 0, Is.True);
  }

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(LifWriter.Build([], volumeLabel: "CWBTST"));
    ms.Position = 0;
    return ms;
  }
}
