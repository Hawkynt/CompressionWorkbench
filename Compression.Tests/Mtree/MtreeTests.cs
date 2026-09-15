using System.Text;
using Compression.Registry;
using FileFormat.Mtree;

namespace Compression.Tests.Mtree;

[TestFixture]
public class MtreeTests {
  [Test]
  [Category("RoundTrip")]
  public void WriterReader_RoundTripsMetadataAndEscapedNames() {
    using var stream = new MemoryStream();
    using (var writer = new MtreeWriter(stream, leaveOpen: true)) {
      writer.WriteEntry(new MtreeEntry {
        Path = "dir/file name-ä.txt",
        Type = MtreeEntryType.File,
        Mode = 0x1A4,
        Uid = 1000,
        Gid = 100,
        Size = 123,
        LinkTarget = null,
      });
      writer.Flush();
    }

    stream.Position = 0;
    var entry = new MtreeReader(stream).ReadAll().Single();

    Assert.Multiple(() => {
      Assert.That(entry.Path, Is.EqualTo("dir/file name-ä.txt"));
      Assert.That(entry.Type, Is.EqualTo(MtreeEntryType.File));
      Assert.That(entry.Mode, Is.EqualTo(0x1A4));
      Assert.That(entry.Uid, Is.EqualTo(1000));
      Assert.That(entry.Gid, Is.EqualTo(100));
      Assert.That(entry.Size, Is.EqualTo(123));
    });
  }

  [Test]
  [Category("Compatibility")]
  public void Reader_AppliesSetUnsetAndClassicDirectoryTraversal() {
    const string manifest = """
      #mtree
      /set type=file uid=1000 gid=100 mode=0644
      dir type=dir mode=0755
      file\040one size=3
      sub type=dir
      child size=4
      ..
      ..
      /unset uid gid
      ./link type=link link=target\040name
      """;

    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
    var entries = new MtreeReader(stream).ReadAll();

    Assert.That(entries.Select(x => x.Path), Is.EqualTo(new[] {
      "dir",
      "dir/file one",
      "dir/sub",
      "dir/sub/child",
      "link",
    }));
    Assert.Multiple(() => {
      Assert.That(entries[1].Uid, Is.EqualTo(1000));
      Assert.That(entries[1].Mode, Is.EqualTo(0x1A4));
      Assert.That(entries[3].Size, Is.EqualTo(4));
      Assert.That(entries[4].Type, Is.EqualTo(MtreeEntryType.Link));
      Assert.That(entries[4].LinkTarget, Is.EqualTo("target name"));
      Assert.That(entries[4].Uid, Is.Null);
    });
  }

  [Test]
  [Category("Registry")]
  public void Descriptor_CreateProducesListableManifestWithoutPretendingToContainBodies() {
    var descriptor = new MtreeFormatDescriptor();
    using var stream = new MemoryStream();
    descriptor.Create(
      stream,
      [
        new ArchiveInputInfo("dir", "dir", IsDirectory: true),
        ArchiveInputInfo.InMemory("dir/payload.bin", [1, 2, 3, 4]),
      ],
      new FormatCreateOptions());

    var bytes = stream.ToArray();
    Assert.That(Encoding.UTF8.GetString(bytes), Does.StartWith("#mtree\n"));

    stream.Position = 0;
    var listed = descriptor.List(stream, password: null);
    Assert.That(listed, Has.Count.EqualTo(2));
    Assert.Multiple(() => {
      Assert.That(listed[0].Name, Is.EqualTo("dir"));
      Assert.That(listed[0].IsDirectory, Is.True);
      Assert.That(listed[1].Name, Is.EqualTo("dir/payload.bin"));
      Assert.That(listed[1].OriginalSize, Is.EqualTo(4));
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.False);
    });

    stream.Position = 0;
    Assert.Throws<NotSupportedException>(() => descriptor.Extract(stream, Path.GetTempPath(), null, null));
  }

  [Test]
  [Category("Compatibility")]
  public void Reader_DecodesUtf8OctalByteEscapes() {
    // UTF-8 ä is C3 A4; mtree escapes bytes, not Unicode code points.
    const string manifest = "#mtree\n./caf\\303\\244 type=file size=0\n";
    using var stream = new MemoryStream(Encoding.ASCII.GetBytes(manifest));
    var entry = new MtreeReader(stream).ReadAll().Single();
    Assert.That(entry.Path, Is.EqualTo("cafä"));
  }
}
