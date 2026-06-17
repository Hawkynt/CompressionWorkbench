using System;
using System.IO;
using Compression.Registry;
using FileSystem.Stacker;

namespace Compression.Tests.Stacker;

[TestFixture]
public class StackerDetectionTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new StackerFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Stacker"));
    Assert.That(d.DisplayName, Is.EqualTo("Stacker CVF"));
    Assert.That(d.Extensions, Does.Contain(".sta"));
    Assert.That(d.Extensions, Does.Contain(".stk"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  // A self-produced STACVOL: banner + Stacker Control Block (BPB) + inner FAT12.
  [Test, Category("HappyPath")]
  public void Detect_WrittenVolume_ParsesBannerAndScb() {
    var w = new StackerWriter { Version = 3, VolumePath = "C:\\STACVOL.DSK" };
    w.AddFile("HELLO.TXT", System.Text.Encoding.ASCII.GetBytes("hello stacker"));
    var img = w.Build();

    using var r = new StackerReader(new MemoryStream(img));
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Version, Is.EqualTo(3));
    Assert.That(r.VolumeName, Does.Contain("STACVOL.DSK"));
    Assert.That(r.SectorsPerCluster, Is.GreaterThan(0));
    Assert.That(r.NumberOfFats, Is.EqualTo(2));
    Assert.That(r.RootEntries, Is.EqualTo(512));
    Assert.That(r.Entries, Has.Some.Matches<StackerEntry>(e => e.Name == "HELLO.TXT"));
  }

  // Detection grounded in a REAL Stacker volume produced by the genuine Stacker
  // 3.10 CREATE tool under MS-DOS 6.22, staged at ~/.cw-qemu/real_stacker_v3.dsk.
  [Test, Category("ExternalInterop")]
  public void Detect_RealStackerVolume() {
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var path = Path.Combine(home, ".cw-qemu", "real_stacker_v3.dsk");
    if (!File.Exists(path))
      Assert.Ignore("Real Stacker STACVOL not staged at ~/.cw-qemu/real_stacker_v3.dsk.");
    using var r = new StackerReader(File.OpenRead(path));
    Assert.That(r.ValidHeader, Is.True, "our reader must recognize a genuine Stacker STACVOL.");
    Assert.That(r.Version, Is.EqualTo(3));
    Assert.That(r.VolumeName, Does.Contain("STACVOL.DSK"));
    // Genuine SCB/BPB fields verified byte-for-byte against the oracle image.
    Assert.That(r.SectorsPerCluster, Is.EqualTo(16));
    Assert.That(r.NumberOfFats, Is.EqualTo(2));
    Assert.That(r.SectorsPerFat, Is.EqualTo(12));
    Assert.That(r.RootEntries, Is.EqualTo(512));
  }

  [Test, Category("Sad")]
  public void Detect_NotStacker_HasNoValidHeader() {
    var img = new byte[2048];
    img[0] = 0xFF; img[1] = 0xFF; img[2] = 0xFF;
    using var r = new StackerReader(new MemoryStream(img));
    Assert.That(r.ValidHeader, Is.False);
    Assert.That(r.Entries, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var w = new StackerWriter();
    w.AddFile("DATA.BIN", new byte[] { 1, 2, 3, 4, 5 });
    var img = w.Build();

    var d = new StackerFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Some.Matches<ArchiveEntryInfo>(e => e.Name == "DATA.BIN"));
  }
}
