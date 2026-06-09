using System.Text;
using Compression.Registry;
using FileFormat.Sfar;

namespace Compression.Tests.Sfar;

/// <summary>
/// WORM contract tests for the stored-mode SFAR writer: the descriptor must emit
/// archives that round-trip through <see cref="SfarReader"/> with original names,
/// sizes and contents intact, and the on-disk header must carry the canonical
/// magic / version / compression tag.
/// </summary>
[TestFixture]
public class SfarWormTests {

  private static byte[] CreateArchive(IEnumerable<(string Name, byte[] Data)> entries) {
    var d = new SfarFormatDescriptor();
    var inputs = entries.Select(e => ArchiveInputInfo.InMemory(e.Name, e.Data)).ToList();
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());
    return ms.ToArray();
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_SingleEntry_RoundTripsThroughReader() {
    var payload = "Mass Effect 3 DLC payload"u8.ToArray();
    var bytes = CreateArchive([("dlc/file.bin", payload)]);

    using var ms = new MemoryStream(bytes);
    using var r = new SfarReader(ms);

    // entry 0 is the synthetic Filenames.txt manifest, entry 1 is the real file
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.IsLzxCompressed, Is.False);
    Assert.That(r.Entries[1].Name, Is.EqualTo("dlc/file.bin"));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_MultipleEntries_RoundTrip() {
    var p1 = Encoding.UTF8.GetBytes("FIRST entry");
    var p2 = Enumerable.Repeat((byte)0xAB, 1500).ToArray();
    var p3 = "third"u8.ToArray();
    var bytes = CreateArchive([
      ("alpha.bin", p1),
      ("nested/beta.bin", p2),
      ("zeta.bin", p3),
    ]);

    using var ms = new MemoryStream(bytes);
    using var r = new SfarReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(4));
    Assert.That(r.Entries[1].Name, Is.EqualTo("alpha.bin"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("nested/beta.bin"));
    Assert.That(r.Entries[3].Name, Is.EqualTo("zeta.bin"));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(p1));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(p2));
    Assert.That(r.Extract(r.Entries[3]), Is.EqualTo(p3));
  }

  [Test, Category("EdgeCase")]
  public void Create_EmptyInput_EmitsValidHeader() {
    var bytes = CreateArchive([]);

    Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(32));
    Assert.That(bytes[0], Is.EqualTo((byte)'S'));
    Assert.That(bytes[1], Is.EqualTo((byte)'F'));
    Assert.That(bytes[2], Is.EqualTo((byte)'A'));
    Assert.That(bytes[3], Is.EqualTo((byte)'R'));

    using var ms = new MemoryStream(bytes);
    using var r = new SfarReader(ms);
    // only the synthetic Filenames.txt manifest (which itself is empty)
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.IsLzxCompressed, Is.False);
  }

  [Test, Category("HappyPath")]
  public void Create_HeaderTagsStoredCompression() {
    var bytes = CreateArchive([("only.bin", new byte[] { 1, 2, 3, 4 })]);

    // bytes[28..32] = compression tag — must be "\0\0\0\0" for stored mode
    Assert.That(bytes[28], Is.EqualTo((byte)0));
    Assert.That(bytes[29], Is.EqualTo((byte)0));
    Assert.That(bytes[30], Is.EqualTo((byte)0));
    Assert.That(bytes[31], Is.EqualTo((byte)0));
  }

  [Test, Category("HappyPath")]
  public void Create_ThroughDescriptorList_ReportsStoredMethod() {
    var bytes = CreateArchive([("readme.txt", "hello"u8.ToArray())]);

    var d = new SfarFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var entries = d.List(ms, password: null);

    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries.All(e => e.Method == "Stored"), Is.True);
  }
}
