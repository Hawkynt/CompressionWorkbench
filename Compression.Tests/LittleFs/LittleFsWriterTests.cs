using System.Text;
using FileSystem.LittleFs;
using Compression.Registry;

namespace Compression.Tests.LittleFs;

/// <summary>
/// Behaviour of the from-scratch LittleFS WORM writer: a built image must
/// round-trip back through <see cref="LittleFsReader"/> with every file present
/// and its content intact, including files placed in subdirectories.
/// </summary>
[TestFixture]
public class LittleFsWriterTests {

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    var w = new LittleFsWriter();
    foreach (var (name, data) in files)
      w.AddFile(name, data);
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Build_SingleSmallFile_RoundTripsThroughReader() {
    var content = Encoding.ASCII.GetBytes("hello littlefs");
    var image = BuildImage(("readme.txt", content));

    var reader = new LittleFsReader(image);
    var entry = reader.Files.Single(f => f.Path == "readme.txt");
    Assert.That(reader.ReadFile(entry), Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Build_MultipleFiles_AllRoundTrip() {
    var a = Encoding.ASCII.GetBytes("first file");
    var b = Encoding.ASCII.GetBytes("second file content");
    var c = Array.Empty<byte>();
    var image = BuildImage(("a.txt", a), ("b.bin", b), ("empty.dat", c));

    var reader = new LittleFsReader(image);
    var names = reader.Files.Select(f => f.Path).ToHashSet();
    Assert.That(names, Does.Contain("a.txt"));
    Assert.That(names, Does.Contain("b.bin"));
    Assert.That(names, Does.Contain("empty.dat"));

    Assert.That(reader.ReadFile(reader.Files.Single(f => f.Path == "a.txt")), Is.EqualTo(a));
    Assert.That(reader.ReadFile(reader.Files.Single(f => f.Path == "b.bin")), Is.EqualTo(b));
    Assert.That(reader.ReadFile(reader.Files.Single(f => f.Path == "empty.dat")), Is.EqualTo(c));
  }

  [Test, Category("HappyPath")]
  public void Build_FileInSubdirectory_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("nested payload");
    var image = BuildImage(("dir/sub/deep.txt", data), ("root.txt", Encoding.ASCII.GetBytes("at root")));

    var reader = new LittleFsReader(image);
    var paths = reader.Files.Select(f => f.Path).ToHashSet();
    Assert.That(paths, Does.Contain("dir/sub/deep.txt"));
    Assert.That(paths, Does.Contain("root.txt"));
    Assert.That(reader.ReadFile(reader.Files.Single(f => f.Path == "dir/sub/deep.txt")), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Build_LargeFile_UsesCtzAndRoundTrips() {
    // Larger than the inline cap → forces a CTZ skip-list across several blocks.
    var data = new byte[20_000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)((i * 31 + 7) & 0xFF);
    var image = BuildImage(("big.bin", data));

    var reader = new LittleFsReader(image);
    var entry = reader.Files.Single(f => f.Path == "big.bin");
    Assert.That(reader.ReadFile(entry), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Build_ProducesWalkableImage_DescriptorListsRealFiles() {
    var image = BuildImage(("x.txt", Encoding.ASCII.GetBytes("y")));
    using var ms = new MemoryStream(image);
    var d = new LittleFsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("x.txt"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_ProducesImageReadableByReader() {
    var d = new LittleFsFormatDescriptor();
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("docs/notes.txt", Encoding.ASCII.GetBytes("notes here")),
      ArchiveInputInfo.InMemory("top.txt", Encoding.ASCII.GetBytes("top level")),
    };
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());

    var reader = new LittleFsReader(ms.ToArray());
    var paths = reader.Files.Select(f => f.Path).ToHashSet();
    Assert.That(paths, Does.Contain("docs/notes.txt"));
    Assert.That(paths, Does.Contain("top.txt"));
    Assert.That(reader.ReadFile(reader.Files.Single(f => f.Path == "top.txt")),
      Is.EqualTo(Encoding.ASCII.GetBytes("top level")));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsArchiveCreatable() {
    Assert.That(new LittleFsFormatDescriptor(), Is.InstanceOf<IArchiveCreatable>());
  }
}
