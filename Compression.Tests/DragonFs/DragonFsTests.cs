using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.DragonFs;

[TestFixture]
public class DragonFsTests {

  // Build a minimal DragonFS image with one file in the root directory.
  // Layout (big-endian throughout):
  //   0x000..0x007  optional "DragonFS" tag (we use it for self-detection)
  //   0x008..0x107  zero padding
  //   0x108..       file entry chain starts at DFS_ROOT_OFFSET = 264 (8 + 256)
  //                 First entry is the file record directly (next=0, flags=0,
  //                 name="hello.txt", file_size=N), followed by file data.
  private static byte[] BuildMinimalImage() {
    var image = new byte[1024];
    // Optional "DragonFS" tag
    "DragonFS"u8.ToArray().CopyTo(image.AsSpan(0));

    var rootEntry = 8 + 256; // 264 — head of the root directory's child chain
    var fileData  = rootEntry + 32; // 296 — data follows entry record

    var content = "Hello DragonFS!"u8.ToArray();

    // File entry: next=0, flags=0 (regular file), name="hello.txt", size=content.Length
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(rootEntry + 0,  4), 0);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(rootEntry + 4,  4), 0);
    var nameBytes = Encoding.ASCII.GetBytes("hello.txt");
    nameBytes.CopyTo(image.AsSpan(rootEntry + 8, 20));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(rootEntry + 28, 4), (uint)content.Length);

    // File data immediately after entry
    content.CopyTo(image.AsSpan(fileData));

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.DragonFs.DragonFsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("DragonFs"));
    Assert.That(d.DisplayName, Is.EqualTo("DragonFS"));
    Assert.That(d.Extensions, Does.Contain(".dfs"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("DragonFS"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Read_MinimalSyntheticImage() {
    var img = BuildMinimalImage();
    using var ms = new MemoryStream(img);
    var r = new FileSystem.DragonFs.DragonFsReader(ms);
    Assert.That(r.ValidRoot, Is.True);
    Assert.That(r.RootOffset, Is.EqualTo(264));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(r.Entries[0].IsDirectory, Is.False);
    Assert.That(r.Entries[0].Size, Is.EqualTo(15));

    var data = r.Extract(r.Entries[0]);
    Assert.That(Encoding.ASCII.GetString(data), Is.EqualTo("Hello DragonFS!"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalImage();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.DragonFs.DragonFsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("hello.txt"));

    var tmp = Path.Combine(Path.GetTempPath(), $"dragonfs-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      ms.Position = 0;
      d.Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "hello.txt")), Is.True);
      Assert.That(File.ReadAllText(Path.Combine(tmp, "hello.txt")), Is.EqualTo("Hello DragonFS!"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("Sad")]
  public void Defragment_Throws() {
    var d = new FileSystem.DragonFs.DragonFsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimalImage());
    Assert.Throws<NotSupportedException>(() => d.Defragment(ms));
  }

  [Test, Category("HappyPath")]
  public void Implements_Creatable_Interface() {
    var d = new FileSystem.DragonFs.DragonFsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }
}
