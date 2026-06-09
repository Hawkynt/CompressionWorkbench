using System.Text;
using Compression.Registry;
using FileFormat.AppImage;

namespace Compression.Tests.AppImage;

[TestFixture]
public class AppImageWriterTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var payload = "#!/bin/sh\nexec ./hello\n"u8.ToArray();

    using var ms = new MemoryStream();
    using (var w = new AppImageWriter(ms, leaveOpen: true))
      w.AddFile("AppRun", payload);
    ms.Position = 0;

    var d = new AppImageFormatDescriptor();
    var list = d.List(ms, null);
    Assert.That(list[0].Name, Is.EqualTo("metadata.ini"));
    Assert.That(list.Any(e => e.Name == "filesystem/AppRun"), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    using var ms = new MemoryStream();
    using (var w = new AppImageWriter(ms, leaveOpen: true)) {
      w.AddFile("AppRun", Encoding.UTF8.GetBytes("#!/bin/sh\n"));
      w.AddFile("hello.desktop", Encoding.UTF8.GetBytes("[Desktop Entry]\nName=Hello\n"));
      w.AddFile("usr/share/icons/hello.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 });
    }
    ms.Position = 0;

    var d = new AppImageFormatDescriptor();
    var list = d.List(ms, null);
    var fsNames = list.Where(e => e.Name.StartsWith("filesystem/"))
                      .Select(e => e.Name).ToList();
    Assert.That(fsNames, Does.Contain("filesystem/AppRun"));
    Assert.That(fsNames, Does.Contain("filesystem/hello.desktop"));
    Assert.That(fsNames.Any(n => n.Contains("hello.png")), Is.True);
  }

  [Test, Category("HappyPath")]
  public void ElfStub_HasAi2MarkerAtOffsetEight() {
    using var ms = new MemoryStream();
    using (var w = new AppImageWriter(ms, leaveOpen: true))
      w.AddFile("foo", "bar"u8.ToArray());

    var bytes = ms.ToArray();
    Assert.That(bytes[0], Is.EqualTo(0x7F));
    Assert.That(bytes[1], Is.EqualTo((byte)'E'));
    Assert.That(bytes[2], Is.EqualTo((byte)'L'));
    Assert.That(bytes[3], Is.EqualTo((byte)'F'));
    Assert.That(bytes[8], Is.EqualTo((byte)'A'));
    Assert.That(bytes[9], Is.EqualTo((byte)'I'));
    Assert.That(bytes[10], Is.EqualTo((byte)0x02));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate() {
    var d = new AppImageFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsViaList() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("AppRun", "#!/bin/sh\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("usr/bin/program", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
    };

    using var ms = new MemoryStream();
    var d = new AppImageFormatDescriptor();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var list = d.List(ms, null);
    Assert.That(list[0].Name, Is.EqualTo("metadata.ini"));
    Assert.That(list.Any(e => e.Name == "filesystem/AppRun"), Is.True);
    Assert.That(list.Any(e => e.Name == "filesystem/usr/bin/program"), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_StripsFilesystemPrefixOnReround() {
    // Mimic a List→Extract→Create cycle: caller passes back the "filesystem/" names.
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("filesystem/AppRun", "#!/bin/sh\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("metadata.ini", "[appimage]\n"u8.ToArray()),  // should be dropped
    };

    using var ms = new MemoryStream();
    var d = new AppImageFormatDescriptor();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var list = d.List(ms, null);
    // metadata.ini still surfaces (synthetic), but it's not duplicated, and AppRun lives at filesystem/AppRun.
    Assert.That(list.Count(e => e.Name == "metadata.ini"), Is.EqualTo(1));
    Assert.That(list.Any(e => e.Name == "filesystem/AppRun"), Is.True);
    // metadata.ini must NOT appear inside the filesystem/ branch.
    Assert.That(list.Any(e => e.Name == "filesystem/metadata.ini"), Is.False);
  }

  [Test, Category("EdgeCase")]
  public void ElfStub_Size_Is64Bytes() {
    Assert.That(AppImageWriter.StubSize, Is.EqualTo(64));
    Assert.That(AppImageWriter.AppImageType, Is.EqualTo((byte)0x02));
  }
}
