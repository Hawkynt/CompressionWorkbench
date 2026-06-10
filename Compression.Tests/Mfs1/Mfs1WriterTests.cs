#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Mfs1;

namespace Compression.Tests.Mfs1;

/// <summary>
/// WORM tests for <see cref="Mfs1Writer"/>: an empty image, single file, multi-
/// file catalog and a round-trip through <see cref="Mfs1Reader"/>. Pins that
/// every byte the writer emits is recognized by the reader without losing data.
/// </summary>
[TestFixture]
public class Mfs1WriterTests {

  [Test, Category("HappyPath")]
  public void Build_EmptyCatalog_TrackAligned_AndReaderSeesNoEntries() {
    var w = new Mfs1Writer();
    var img = w.Build("EMPTY", totalSectors: 10);

    Assert.That(img.Length, Is.EqualTo(10 * 256));
    Assert.That(img[256 + 5], Is.EqualTo((byte)0), "Zero entries → entry-count*8 byte is 0.");

    var r = new Mfs1Reader(img);
    Assert.That(r.Entries, Is.Empty);
    Assert.That(r.DiskTitle, Does.Contain("EMPTY"));
  }

  [Test, Category("HappyPath")]
  public void Build_SingleFile_RoundTripsExactBytes() {
    var payload = "Hello MFS-1 writer!"u8.ToArray();
    var w = new Mfs1Writer();
    w.AddFile("HELLO", payload);
    var img = w.Build("ONEFILE");

    var r = new Mfs1Reader(img);
    Assert.That(r.CatalogParsed, Is.True);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO"));
    Assert.That(r.Entries[0].Directory, Is.EqualTo('$'));
    Assert.That(r.Entries[0].Size, Is.EqualTo((uint)payload.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Build_MultipleFiles_AllRoundTrip() {
    var a = Encoding.ASCII.GetBytes("alpha-payload-bytes-here");
    var b = Encoding.ASCII.GetBytes("Second file body, slightly larger than the first one.");
    var c = new byte[300]; // spans two sectors
    for (var i = 0; i < c.Length; i++) c[i] = (byte)(i * 7 & 0xFF);

    var w = new Mfs1Writer();
    w.AddFile("ALPHA", a);
    w.AddFile("BRAVO", b);
    w.AddFile("CHARLIE", c);
    var img = w.Build("MULTI");

    var r = new Mfs1Reader(img);
    Assert.That(r.CatalogParsed, Is.True);
    Assert.That(r.Entries.Select(e => e.Name).OrderBy(s => s).ToList(),
      Is.EqualTo(new[] { "ALPHA", "BRAVO", "CHARLIE" }));

    // Extract by name lookup to side-step DFS catalog order.
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "ALPHA")), Is.EqualTo(a));
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "BRAVO")), Is.EqualTo(b));
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "CHARLIE")), Is.EqualTo(c));
  }

  [Test, Category("HappyPath")]
  public void Build_DirectoryPrefix_IsHonoured() {
    var payload = "in-a-directory"u8.ToArray();
    var w = new Mfs1Writer();
    w.AddFile("PROG", payload, directory: 'A');
    var img = w.Build();

    var r = new Mfs1Reader(img);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("PROG"));
    Assert.That(r.Entries[0].Directory, Is.EqualTo('A'));
    Assert.That(r.Entries[0].FullName, Is.EqualTo("A.PROG"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_ProducesReadableImage() {
    var d = new Mfs1FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>(),
      "Mfs1 descriptor must advertise IArchiveCreatable.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);

    var payload = "via-descriptor-Create()"u8.ToArray();
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("HELLO", payload),
    };

    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(ms, inputs, new FormatCreateOptions());
    var img = ms.ToArray();

    var r = new Mfs1Reader(img);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));

    using var ms2 = new MemoryStream(img, writable: false);
    var entries = d.List(ms2, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("HELLO"));
  }

  [Test, Category("BoundaryAnalysis")]
  public void Build_31Files_FillsCatalog() {
    var w = new Mfs1Writer();
    for (var i = 0; i < 31; i++)
      w.AddFile($"F{i:D2}", new byte[] { (byte)i });
    var img = w.Build("FULLCAT");
    var r = new Mfs1Reader(img);
    Assert.That(r.Entries, Has.Count.EqualTo(31));
  }

  [Test, Category("BoundaryAnalysis")]
  public void Build_32ndFile_Throws() {
    var w = new Mfs1Writer();
    for (var i = 0; i < 32; i++)
      w.AddFile($"F{i:D2}", new byte[] { (byte)i });
    Assert.Throws<InvalidOperationException>(() => w.Build("OVRFLOW"));
  }

  [Test, Category("HappyPath")]
  public void Build_ZeroLengthFile_RoundTrips() {
    var w = new Mfs1Writer();
    w.AddFile("EMPTYF", System.Array.Empty<byte>());
    var img = w.Build();
    var r = new Mfs1Reader(img);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(0u));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(System.Array.Empty<byte>()));
  }
}
