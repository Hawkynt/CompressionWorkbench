#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Lnk;

namespace Compression.Tests.Lnk;

[TestFixture]
public class LnkWriterTests {

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate() {
    var d = new LnkFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d is IArchiveCreatable, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Write_EmitsCanonicalHeaderAndClsid() {
    using var ms = new MemoryStream();
    LnkWriter.Write(ms, @"C:\Windows\System32\notepad.exe");
    var blob = ms.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0, 4)), Is.EqualTo(0x0000004Cu));
    // CLSID first byte at offset 4 is 0x01 0x14 0x02 0x00.
    Assert.That(blob[4], Is.EqualTo((byte)0x01));
    Assert.That(blob[5], Is.EqualTo((byte)0x14));
    Assert.That(blob[6], Is.EqualTo((byte)0x02));
    Assert.That(blob[7], Is.EqualTo((byte)0x00));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_TargetPath_VisibleInLinkInfo() {
    using var ms = new MemoryStream();
    LnkWriter.Write(ms, @"C:\Some\Target\File.txt");

    using var read = new MemoryStream(ms.ToArray());
    var entries = new LnkFormatDescriptor().List(read, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Contains.Item("FULL.lnk"));
    Assert.That(names, Contains.Item("metadata.ini"));
    Assert.That(names, Contains.Item("header.bin"));
    Assert.That(names, Contains.Item("linkinfo.bin"));

    // metadata.ini surfaces the target via target_path_from_linkinfo_if_set.
    read.Position = 0;
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try {
      new LnkFormatDescriptor().Extract(read, tmpDir, null, null);
      var ini = File.ReadAllText(Path.Combine(tmpDir, "metadata.ini"));
      Assert.That(ini, Does.Contain("target_path_from_linkinfo_if_set=C:\\Some\\Target\\File.txt"));
    } finally {
      Directory.Delete(tmpDir, recursive: true);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_WritesLnkPointingAtFirstInput() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "payload"u8.ToArray());
      var inputs = new[] {
        new ArchiveInputInfo(tmp, Path.GetFileName(tmp), IsDirectory: false),
      };
      using var outStream = new MemoryStream();
      new LnkFormatDescriptor().Create(outStream, inputs, new FormatCreateOptions());

      outStream.Position = 0;
      var entries = new LnkFormatDescriptor().List(outStream, null);
      Assert.That(entries.Any(e => e.Name == "FULL.lnk"), Is.True);
      Assert.That(entries.Any(e => e.Name == "linkinfo.bin"), Is.True);
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("HappyPath")]
  public void Write_WithUnicodeStrings_EmitsStringDataBlocks() {
    using var ms = new MemoryStream();
    LnkWriter.Write(ms, @"C:\foo.exe",
      workingDir: @"C:\WorkDir",
      arguments: "--help",
      iconLocation: @"C:\Icons\foo.ico");

    var blob = ms.ToArray();
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(20, 4));
    // HasLinkInfo(2) | HasWorkingDir(0x10) | HasArguments(0x20) | HasIconLocation(0x40) | IsUnicode(0x80).
    Assert.That((flags & 0x02u), Is.Not.EqualTo(0u), "HasLinkInfo");
    Assert.That((flags & 0x10u), Is.Not.EqualTo(0u), "HasWorkingDir");
    Assert.That((flags & 0x20u), Is.Not.EqualTo(0u), "HasArguments");
    Assert.That((flags & 0x40u), Is.Not.EqualTo(0u), "HasIconLocation");
    Assert.That((flags & 0x80u), Is.Not.EqualTo(0u), "IsUnicode");

    using var ms2 = new MemoryStream(blob);
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try {
      new LnkFormatDescriptor().Extract(ms2, tmpDir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(tmpDir, "strings/working_dir.txt")), Is.EqualTo(@"C:\WorkDir"));
      Assert.That(File.ReadAllText(Path.Combine(tmpDir, "strings/arguments.txt")), Is.EqualTo("--help"));
      Assert.That(File.ReadAllText(Path.Combine(tmpDir, "strings/icon_location.txt")), Is.EqualTo(@"C:\Icons\foo.ico"));
    } finally {
      Directory.Delete(tmpDir, recursive: true);
    }
  }

  // Boundary: empty input list throws — a Shell Link must target something.
  [Test, Category("Exception")]
  public void Descriptor_Create_NoInputs_Throws() {
    using var outStream = new MemoryStream();
    Assert.That(
      () => new LnkFormatDescriptor().Create(outStream, [], new FormatCreateOptions()),
      Throws.ArgumentException);
  }

  // EquivalenceClass: pin the honest WORM scope. MS-SHLLINK describes one
  // target whose StringData / ExtraData regions reshape with every mutation,
  // so the descriptor refuses to advertise CanModify / IArchiveModifiable and
  // documents the rebuild semantic in its Description.
  [Test, Category("EquivalenceClass")]
  public void Descriptor_WormState_NoCanModify() {
    var d = new LnkFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(d, Is.Not.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Description, Does.Contain("WORM"));
    Assert.That(d.Description, Does.Contain("IArchiveModifiable"));
  }
}
