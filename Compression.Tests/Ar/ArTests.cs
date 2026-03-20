using FileFormat.Ar;

namespace Compression.Tests.Ar;

[TestFixture]
public class ArTests {

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static byte[] WriteArchive(IReadOnlyList<ArEntry> entries) {
    using var ms = new MemoryStream();
    using var writer = new ArWriter(ms, leaveOpen: true);
    writer.Write(entries);
    return ms.ToArray();
  }

  private static IReadOnlyList<ArEntry> ReadArchive(byte[] archive) {
    using var reader = new ArReader(new MemoryStream(archive));
    return reader.Entries;
  }

  private static IReadOnlyList<ArEntry> RoundTrip(IReadOnlyList<ArEntry> entries) {
    byte[] archive = WriteArchive(entries);
    return ReadArchive(archive);
  }

  // ── Tests ─────────────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile() {
    byte[] data = "Hello, ar!"u8.ToArray();
    var entries = new List<ArEntry> {
      new() { Name = "hello.txt", Data = data },
    };

    var result = RoundTrip(entries);

    Assert.That(result, Has.Count.EqualTo(1));
    Assert.That(result[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(result[0].Data, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MultipleFiles() {
    var entries = new List<ArEntry> {
      new() { Name = "a.txt", Data = "Alpha"u8.ToArray() },
      new() { Name = "b.txt", Data = "Beta"u8.ToArray() },
      new() { Name = "c.txt", Data = "Gamma"u8.ToArray() },
    };

    var result = RoundTrip(entries);

    Assert.That(result, Has.Count.EqualTo(3));
    Assert.That(result[0].Name, Is.EqualTo("a.txt"));
    Assert.That(result[0].Data, Is.EqualTo("Alpha"u8.ToArray()));
    Assert.That(result[1].Name, Is.EqualTo("b.txt"));
    Assert.That(result[1].Data, Is.EqualTo("Beta"u8.ToArray()));
    Assert.That(result[2].Name, Is.EqualTo("c.txt"));
    Assert.That(result[2].Data, Is.EqualTo("Gamma"u8.ToArray()));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyArchive() {
    var result = RoundTrip([]);
    Assert.That(result, Is.Empty);
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LongFilename() {
    // Name longer than 15 chars triggers GNU extended filename table.
    const string longName = "this_is_a_very_long_filename.txt";
    byte[] data = "content"u8.ToArray();
    var entries = new List<ArEntry> {
      new() { Name = longName, Data = data },
    };

    var result = RoundTrip(entries);

    Assert.That(result, Has.Count.EqualTo(1));
    Assert.That(result[0].Name, Is.EqualTo(longName));
    Assert.That(result[0].Data, Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_OddSizeData() {
    // Odd-length data requires a padding byte after the entry data.
    byte[] data = [1, 2, 3, 4, 5]; // length 5 — odd
    var entries = new List<ArEntry> {
      new() { Name = "odd.bin", Data = data },
    };

    var result = RoundTrip(entries);

    Assert.That(result, Has.Count.EqualTo(1));
    Assert.That(result[0].Data, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MetadataPreserved() {
    var modTime = new DateTimeOffset(2001, 9, 11, 8, 46, 0, TimeSpan.Zero);
    var entries = new List<ArEntry> {
      new() {
        Name         = "meta.txt",
        Data         = [0x42],
        ModifiedTime = modTime,
        OwnerId      = 1000,
        GroupId      = 1001,
        FileMode     = 0x81A4, // octal 0100644
      },
    };

    var result = RoundTrip(entries);

    Assert.That(result[0].ModifiedTime, Is.EqualTo(modTime));
    Assert.That(result[0].OwnerId, Is.EqualTo(1000));
    Assert.That(result[0].GroupId, Is.EqualTo(1001));
    Assert.That(result[0].FileMode, Is.EqualTo(0x81A4)); // octal 0100644
  }

  [Category("Exception")]
  [Test]
  public void Reader_InvalidMagic_Throws() {
    byte[] badData = "NOT_ARCH\ngarbage"u8.ToArray();

    Assert.Throws<InvalidDataException>(() => {
      using var _ = new ArReader(new MemoryStream(badData));
    });
  }

  [Category("Exception")]
  [Test]
  public void Writer_NullStream_Throws() {
    Assert.Throws<ArgumentNullException>(() => {
      using var _ = new ArWriter(null!);
    });
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MixedShortAndLongFilenames() {
    var entries = new List<ArEntry> {
      new() { Name = "short.o",                            Data = [1] },
      new() { Name = "very_long_module_name_here.o",       Data = [2] },
      new() { Name = "another.o",                          Data = [3] },
      new() { Name = "yet_another_long_object_filename.o", Data = [4] },
    };

    var result = RoundTrip(entries);

    Assert.That(result, Has.Count.EqualTo(4));
    for (int i = 0; i < entries.Count; ++i) {
      Assert.That(result[i].Name, Is.EqualTo(entries[i].Name));
      Assert.That(result[i].Data, Is.EqualTo(entries[i].Data));
    }
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyEntryData() {
    var entries = new List<ArEntry> {
      new() { Name = "empty.txt", Data = [] },
    };

    var result = RoundTrip(entries);

    Assert.That(result, Has.Count.EqualTo(1));
    Assert.That(result[0].Name, Is.EqualTo("empty.txt"));
    Assert.That(result[0].Data, Is.Empty);
  }
}
