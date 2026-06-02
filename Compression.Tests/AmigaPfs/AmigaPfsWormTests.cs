using System.Text;
using Compression.Registry;
using FileSystem.AmigaPfs;

namespace Compression.Tests.AmigaPfs;

/// <summary>
/// WORM round-trip tests for the PFS3 writer + Stage 1 reader pair.
/// The Stage 1 reader treats an entry's anode number as a direct block pointer
/// and reads <c>Size</c> bytes from that offset; the writer matches the same
/// convention by allocating each file as a contiguous extent. These tests
/// cover the equivalence classes (empty volume, single small file, multi-block
/// file, multiple files, directory marker, large filename) and the boundary
/// cases that the reader/writer pair would care about.
/// </summary>
[TestFixture]
public class AmigaPfsWormTests {

  private static byte[] Build(Action<AmigaPfsWriter> configure, string diskName = "DISK") {
    var w = new AmigaPfsWriter();
    configure(w);
    return w.Build(diskName);
  }

  [Test, Category("HappyPath")]
  public void Writer_EmptyVolume_RoundTrips() {
    var image = Build(_ => { }, diskName: "EMPTY");

    using var ms = new MemoryStream(image);
    var r = new AmigaPfsReader(ms);
    Assert.Multiple(() => {
      Assert.That(r.Signature, Is.EqualTo("PFS\x03"));
      Assert.That(r.DiskName, Is.EqualTo("EMPTY"));
      Assert.That(r.Entries, Is.Empty);
    });
  }

  [Test, Category("HappyPath")]
  public void Writer_SingleSmallFile_RoundTrips() {
    var payload = Encoding.ASCII.GetBytes("Stage 1 round-trip");

    var image = Build(w => w.AddFile("hello.txt", payload));

    using var ms = new MemoryStream(image);
    var r = new AmigaPfsReader(ms);
    Assert.Multiple(() => {
      Assert.That(r.Entries, Has.Count.EqualTo(1));
      Assert.That(r.Entries[0].Name, Is.EqualTo("hello.txt"));
      Assert.That(r.Entries[0].Size, Is.EqualTo(payload.Length));
      Assert.That(r.Entries[0].IsDirectory, Is.False);
      var bytes = r.Extract(r.Entries[0]);
      Assert.That(bytes, Is.EqualTo(payload));
    });
  }

  [Test, Category("Boundary")]
  public void Writer_MultiBlockFile_RoundTrips() {
    // Force the file's extent to span several 512-byte blocks so the
    // writer's contiguous-extent allocation is exercised end-to-end.
    var payload = new byte[1900];
    for (var i = 0; i < payload.Length; i++)
      payload[i] = (byte)(i & 0xFF);

    var image = Build(w => w.AddFile("big.bin", payload));

    using var ms = new MemoryStream(image);
    var r = new AmigaPfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var bytes = r.Extract(r.Entries[0]);
    Assert.That(bytes, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Writer_MultipleFiles_AllRoundTrip() {
    var files = new (string Name, byte[] Data)[] {
      ("a.txt", Encoding.ASCII.GetBytes("alpha")),
      ("b.txt", Encoding.ASCII.GetBytes("beta")),
      ("c.bin", new byte[700]),
    };
    for (var i = 0; i < files[2].Data.Length; i++)
      files[2].Data[i] = (byte)(i * 7 & 0xFF);

    var image = Build(w => {
      foreach (var (name, data) in files)
        w.AddFile(name, data);
    });

    using var ms = new MemoryStream(image);
    var r = new AmigaPfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(files.Length));
    for (var i = 0; i < files.Length; i++) {
      Assert.That(r.Entries[i].Name, Is.EqualTo(files[i].Name), $"name[{i}]");
      Assert.That(r.Extract(r.Entries[i]), Is.EqualTo(files[i].Data), $"data[{i}]");
    }
  }

  [Test, Category("HappyPath")]
  public void Writer_DirectoryEntry_SurfacesInListing() {
    var image = Build(w => {
      w.AddDirectory("docs");
      w.AddFile("readme.md", Encoding.ASCII.GetBytes("read me"));
    });

    using var ms = new MemoryStream(image);
    var r = new AmigaPfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].IsDirectory, Is.True);
    Assert.That(r.Entries[0].Name, Is.EqualTo("docs"));
    Assert.That(r.Entries[1].IsDirectory, Is.False);
    Assert.That(r.Entries[1].Name, Is.EqualTo("readme.md"));
  }

  [Test, Category("Boundary")]
  public void Writer_FilenameAtReaderEntryWidthBoundary_RoundTrips() {
    // Stage 1 dirblock entry: 17 header bytes + nameLen + 1 trailing comment-length byte.
    // Make sure the writer can emit a name that is comfortably long (well past
    // typical AmigaDOS 30-byte names) yet still fits inside a single dirblock.
    var longName = new string('A', 100) + ".txt";
    var payload = Encoding.ASCII.GetBytes("payload");

    var image = Build(w => w.AddFile(longName, payload));

    using var ms = new MemoryStream(image);
    var r = new AmigaPfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo(longName));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_ProducesReadableImage() {
    var d = new AmigaPfsFormatDescriptor();
    var inputs = new[] {
      ArchiveInputInfo.InMemory("alpha.txt", Encoding.ASCII.GetBytes("alpha contents")),
      ArchiveInputInfo.InMemory("beta.bin", Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray()),
    };
    using var output = new MemoryStream();

    d.Create(output, inputs, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "WORK" }
    });

    output.Position = 0;
    var listed = d.List(output, null);
    Assert.That(listed, Has.Count.EqualTo(2));
    Assert.That(listed.Select(e => e.Name), Is.EquivalentTo(new[] { "alpha.txt", "beta.bin" }));

    output.Position = 0;
    Assert.That(Encoding.ASCII.GetString(d.ExtractEntryToMemory(output, "alpha.txt", null)),
      Is.EqualTo("alpha contents"));

    output.Position = 0;
    var bytes = d.ExtractEntryToMemory(output, "beta.bin", null);
    Assert.That(bytes, Has.Length.EqualTo(1024));
    for (var i = 0; i < bytes.Length; i++)
      Assert.That(bytes[i], Is.EqualTo((byte)i), $"beta[{i}]");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_DefaultLabel_WhenOptionMissing() {
    var d = new AmigaPfsFormatDescriptor();
    using var output = new MemoryStream();
    d.Create(output, [ArchiveInputInfo.InMemory("x.txt", [1, 2, 3])], new FormatCreateOptions());

    output.Position = 0;
    var r = new AmigaPfsReader(output);
    Assert.That(r.DiskName, Is.EqualTo("DISK"));
  }

  [Test, Category("Sad")]
  public void Writer_RejectsNullName() {
    var w = new AmigaPfsWriter();
    Assert.Throws<ArgumentNullException>(() => w.AddFile(null!, [1, 2, 3]));
  }

  [Test, Category("Sad")]
  public void Writer_RejectsNullData() {
    var w = new AmigaPfsWriter();
    Assert.Throws<ArgumentNullException>(() => w.AddFile("x", null!));
  }

  [Test, Category("Sad")]
  public void Writer_RejectsBadBlockSize() {
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AmigaPfsWriter(blockSize: 0));
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AmigaPfsWriter(blockSize: 100)); // non-power-of-two
  }

  [Test, Category("Sad")]
  public void Writer_RejectsRootBlockInsideBootBlock() {
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AmigaPfsWriter(rootBlock: 0));
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AmigaPfsWriter(rootBlock: 1));
  }

  [Test, Category("HappyPath")]
  public void Writer_TruncatesOverlongVolumeLabel() {
    var image = Build(_ => { }, diskName: new string('Z', 100));
    using var ms = new MemoryStream(image);
    var r = new AmigaPfsReader(ms);
    Assert.That(r.DiskName.Length, Is.LessThanOrEqualTo(31));
    Assert.That(r.DiskName, Does.StartWith("Z"));
  }
}
