using System.IO.Compression;
using Compression.Registry;
using FileSystem.Jffs1;

namespace Compression.Tests.Jffs1;

/// <summary>
/// The JFFS v1 writer. Given the tree <c>mkfs.jffs</c> was given, in the order it numbered the
/// inodes, the writer must reproduce both reference images byte for byte.
/// </summary>
[TestFixture]
public class Jffs1WriterTests {
  private static byte[] Image(string name) {
    using var res = typeof(Jffs1WriterTests).Assembly.GetManifestResourceStream($"Jffs1Vectors.{name}.img.gz")
      ?? throw new InvalidOperationException($"{name}.img.gz not embedded");
    using var gz = new GZipStream(res, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>The tree an image holds, children in inode order (the order <c>mkfs.jffs</c> met them).</summary>
  private static Jffs1WriteNode Tree(Jffs1Reader r) {
    Jffs1WriteNode Make(Jffs1Entry e, string name) => new(name, e.Mode) {
      Uid = e.Uid, Gid = e.Gid, ATime = e.ATime, MTime = e.MTime, CTime = e.CTime, Data = e.Data,
      Children = e.IsDirectory ? Children(e.Path) : [],
    };
    List<Jffs1WriteNode> Children(string parent) => [.. r.Entries
      .Where(c => c.Path.Length > parent.Length && (parent.Length == 0 ? !c.Path.Contains('/') : c.Path.StartsWith(parent + "/", StringComparison.Ordinal) && !c.Path[(parent.Length + 1)..].Contains('/')))
      .OrderBy(c => c.Ino)
      .Select(c => Make(c, c.Path[(c.Path.LastIndexOf('/') + 1)..]))];
    return Make(r.Root, "");
  }

  [TestCase("jffs1-le", false)]
  [TestCase("jffs1-be", true)]
  [Category("ThirdPartyVector")]
  public void GivenTheTreeMkfsJffsWasGiven_WhenWritten_ThenTheImageIsByteForByteMkfsJffs(string name, bool bigEndian) {
    var reference = Image(name);
    var tree = Tree(new Jffs1Reader(new MemoryStream(reference)));
    using var ms = new MemoryStream();
    Jffs1Writer.Write(ms, tree, bigEndian);
    Assert.That(ms.ToArray(), Is.EqualTo(reference));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(Jffs1Writer.DefaultChunkSize)]
  [TestCase(Jffs1Writer.DefaultChunkSize + 1)]
  [TestCase(3 * Jffs1Writer.DefaultChunkSize + 7)]
  [Category("Boundary")]
  public void GivenAFileAroundTheChunkSize_WhenWrittenAndRead_ThenIdentical(int size) {
    var data = new byte[size];
    new Random(size).NextBytes(data);
    var root = new Jffs1WriteNode("", 0x41ED) {
      Children = [new("f.bin", 0x81A4) { Data = data, MTime = 1714979290 }, new("d", 0x41ED) { Children = [new("link", 0xA1FF) { Data = "../f.bin"u8.ToArray() }] }],
    };
    using var ms = new MemoryStream();
    Jffs1Writer.Write(ms, root);
    var r = new Jffs1Reader(new MemoryStream(ms.ToArray()));
    Assert.That(r.Entries.Select(e => e.Path), Is.EqualTo(new[] { "d", "d/link", "f.bin" }));
    Assert.That(r.Entries.Single(e => e.Path == "f.bin").Data, Is.EqualTo(data));
    Assert.That(r.Entries.Single(e => e.Path == "d/link").IsSymlink, Is.True);
    Assert.That(r.SkippedNodes, Is.Zero);
  }

  [TestCase("", TestName = "Empty name")]
  [TestCase("a/b", TestName = "Name with a slash")]
  [TestCase("..", TestName = "Dot-dot name")]
  [TestCase("日本", TestName = "Name outside Latin-1")]
  [Category("Exception")]
  public void GivenAnUnstorableName_WhenWritten_ThenRefused(string name)
    => Assert.Throws<ArgumentException>(() => Jffs1Writer.Write(new MemoryStream(), new("", 0x41ED) { Children = [new(name, 0x81A4)] }));

  [Test, Category("Exception")]
  public void GivenADeviceNode_WhenWritten_ThenRefused()
    => Assert.Throws<ArgumentException>(() => Jffs1Writer.Write(new MemoryStream(), new("", 0x41ED) { Children = [new("tty", 0x21A4)] }));

  [Test, Category("Exception")]
  public void GivenAFileAsRoot_WhenWritten_ThenRefused()
    => Assert.Throws<ArgumentException>(() => Jffs1Writer.Write(new MemoryStream(), new("", 0x81A4)));

  [Test, Category("HappyPath")]
  public void GivenInputs_WhenCreatedByTheDescriptor_ThenTheImageListsThem() {
    using var ms = new MemoryStream();
    new Jffs1FormatDescriptor().Create(ms, [
      new ArchiveInputInfo("", "docs/", true),
      ArchiveInputInfo.InMemory("docs/readme.txt", "hello jffs\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("top.bin", [1, 2, 3]),
    ], new FormatCreateOptions());
    ms.Position = 0;
    var listing = new Jffs1FormatDescriptor().List(ms, null).Select(e => $"{e.Name}|{e.IsDirectory}|{e.OriginalSize}");
    Assert.That(listing, Is.EquivalentTo(new[] { "docs|True|0", "docs/readme.txt|False|11", "top.bin|False|3" }));
  }
}
