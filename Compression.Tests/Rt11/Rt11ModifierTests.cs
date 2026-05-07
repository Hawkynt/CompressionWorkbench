#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Rt11;

namespace Compression.Tests.Rt11;

[TestFixture]
public class Rt11ModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildEmptyImage();
    Rt11Modifier.AddFile(ms, "GREET.TXT", "hello-rt11"u8.ToArray());
    ms.Position = 0;

    var v = Rt11Reader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    var entry = v.Files.Single(f => f.Name == "GREET.TXT");
    var data = Rt11Reader.Extract(v, entry);
    Assert.That(Encoding.ASCII.GetString(data.AsSpan(0, "hello-rt11".Length)), Is.EqualTo("hello-rt11"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeFile_SpansMultipleBlocks() {
    var ms = BuildEmptyImage();
    var data = new byte[2000]; // 4 blocks
    for (var i = 0; i < data.Length; i++) data[i] = (byte)((i * 11) & 0xFF);
    Rt11Modifier.AddFile(ms, "BIG.DAT", data);

    ms.Position = 0;
    var v = Rt11Reader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    var entry = v.Files.Single(f => f.Name == "BIG.DAT");
    var extracted = Rt11Reader.Extract(v, entry);
    Assert.That(extracted.Length, Is.EqualTo(2048)); // 4 * 512
    Assert.That(extracted.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("RoundTrip")]
  public void AddFile_MultipleFiles_AllReadBack() {
    var ms = BuildEmptyImage();
    Rt11Modifier.AddFile(ms, "A.TXT", "alpha"u8.ToArray());
    Rt11Modifier.AddFile(ms, "B.TXT", "bravo"u8.ToArray());
    Rt11Modifier.AddFile(ms, "C.DAT", new byte[1024]);

    ms.Position = 0;
    var v = Rt11Reader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    Assert.That(v.Files.Select(f => f.Name), Is.EquivalentTo(new[] { "A.TXT", "B.TXT", "C.DAT" }));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_FreesAndAllowsReuse() {
    var ms = BuildEmptyImage();
    Rt11Modifier.AddFile(ms, "OLD.DAT", new byte[1000]);
    Assert.That(Rt11Modifier.RemoveFile(ms, "OLD.DAT"), Is.True);
    Rt11Modifier.AddFile(ms, "NEW.DAT", new byte[1000]);

    ms.Position = 0;
    var v = Rt11Reader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    Assert.That(v.Files.Any(f => f.Name == "OLD.DAT"), Is.False);
    Assert.That(v.Files.Any(f => f.Name == "NEW.DAT"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyImage();
    Assert.That(Rt11Modifier.RemoveFile(ms, "GHOST.X"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_WipesDataBytes() {
    var ms = BuildEmptyImage();
    Rt11Modifier.AddFile(ms, "SECRET.DAT", "TOPSECRET-MARKER-RT11"u8.ToArray());
    Rt11Modifier.RemoveFile(ms, "SECRET.DAT");

    var asAscii = Encoding.ASCII.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-RT11"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveAdd_MergesFreeSpaceCorrectly() {
    var ms = BuildEmptyImage();
    // Lay down three small files, then remove the middle, then add back a
    // bigger one — the new file must fit (free space coalesces with the tail
    // empty area on remove via merge).
    Rt11Modifier.AddFile(ms, "A.X", new byte[512]);
    Rt11Modifier.AddFile(ms, "B.X", new byte[512]);
    Rt11Modifier.AddFile(ms, "C.X", new byte[512]);
    Assert.That(Rt11Modifier.RemoveFile(ms, "B.X"), Is.True);
    // Add a 2-block file; only fits if free space is correctly tracked.
    Rt11Modifier.AddFile(ms, "BIG.X", new byte[1024]);

    ms.Position = 0;
    var v = Rt11Reader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    Assert.That(v.Files.Select(f => f.Name), Is.EquivalentTo(new[] { "A.X", "C.X", "BIG.X" }));
  }

  [Test, Category("EdgeCase")]
  public void AddFile_InvalidRad50_Throws() {
    var ms = BuildEmptyImage();
    Assert.That(
      () => Rt11Modifier.AddFile(ms, "FILE_X.DAT", new byte[1]),
      Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("RAD-50"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddRemoveViaInterface_RoundTrips() {
    var ms = BuildEmptyImage();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      var desc = new Rt11FormatDescriptor();
      ((IArchiveModifiable)desc).Add(ms, [new ArchiveInputInfo(tmp, "VIAIF.X", false)]);

      ms.Position = 0;
      var v = Rt11Reader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
      Assert.That(v.Files.Any(f => f.Name == "VIAIF.X"), Is.True);

      ((IArchiveModifiable)desc).Remove(ms, ["VIAIF.X"]);

      ms.Position = 0;
      v = Rt11Reader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
      Assert.That(v.Files.Any(f => f.Name == "VIAIF.X"), Is.False);
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Capabilities_IncludesCanModify() {
    var desc = new Rt11FormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    ms.Write(Rt11Writer.Build([]));
    ms.Position = 0;
    return ms;
  }
}
