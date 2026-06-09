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
    var w = new Mfs1Writer().SetTitle("EMPTY");
    var img = w.Build();

    // Single-track minimum (10 × 256 = 2560 bytes).
    Assert.That(img.Length, Is.EqualTo(10 * 256));
    Assert.That(img[256 + 5], Is.EqualTo((byte)0), "Zero entries → entry-count*8 byte is 0.");

    var r = new Mfs1Reader(img);
    Assert.That(r.Entries, Is.Empty);
    Assert.That(r.DiskTitle, Does.Contain("EMPTY"));
  }

  [Test, Category("HappyPath")]
  public void Build_SingleFile_RoundTripsExactBytes() {
    var payload = "Hello MFS-1 writer!"u8.ToArray();
    var img = new Mfs1Writer().SetTitle("ONEFILE").AddFile("HELLO", payload).Build();

    var r = new Mfs1Reader(img);
    Assert.That(r.CatalogParsed, Is.True);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO"));
    Assert.That(r.Entries[0].Directory, Is.EqualTo('$'));
    Assert.That(r.Entries[0].Size, Is.EqualTo((uint)payload.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Build_MultipleFiles_AllRoundTrip_AndPreserveOrder() {
    var a = Encoding.ASCII.GetBytes("alpha-payload-bytes-here");
    var b = Encoding.ASCII.GetBytes("Second file body, slightly larger than the first one.");
    var c = new byte[300]; // spans two sectors
    for (var i = 0; i < c.Length; i++) c[i] = (byte)(i * 7 & 0xFF);

    var img = new Mfs1Writer()
      .SetTitle("MULTI")
      .AddFile("ALPHA", a)
      .AddFile("BRAVO", b)
      .AddFile("CHARLIE", c)
      .Build();

    var r = new Mfs1Reader(img);
    Assert.That(r.CatalogParsed, Is.True);
    Assert.That(r.Entries.Select(e => e.Name).ToList(),
      Is.EqualTo(new[] { "ALPHA", "BRAVO", "CHARLIE" }));

    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(a));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(b));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(c));
  }

  [Test, Category("HappyPath")]
  public void Build_DirectoryPrefix_IsHonoured() {
    var payload = "in-a-directory"u8.ToArray();
    var img = new Mfs1Writer().AddFile("A.PROG", payload).Build();

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

    // Round-trip through the descriptor's List/Extract too.
    using var ms2 = new MemoryStream(img, writable: false);
    var entries = d.List(ms2, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("HELLO"));
  }

  [Test, Category("BoundaryAnalysis")]
  public void Build_31Files_FillsCatalog() {
    var w = new Mfs1Writer().SetTitle("FULLCAT");
    for (var i = 0; i < 31; i++)
      w.AddFile($"F{i:D2}", new byte[] { (byte)i });
    var img = w.Build();
    var r = new Mfs1Reader(img);
    Assert.That(r.Entries, Has.Count.EqualTo(31));
  }

  [Test, Category("BoundaryAnalysis")]
  public void Build_32ndFile_Throws() {
    var w = new Mfs1Writer().SetTitle("OVRFLOW");
    for (var i = 0; i < 31; i++)
      w.AddFile($"F{i:D2}", new byte[] { (byte)i });
    Assert.Throws<InvalidOperationException>(() => w.AddFile("OVRFL", new byte[] { 0xFF }));
  }

  [Test, Category("ErrorHandling")]
  public void Build_NameTooLong_Throws() {
    var w = new Mfs1Writer();
    Assert.Throws<ArgumentException>(() => w.AddFile("EIGHT123", new byte[1]));
  }

  [Test, Category("ErrorHandling")]
  public void Build_NonPrintableName_Throws() {
    var w = new Mfs1Writer();
    Assert.Throws<ArgumentException>(() => w.AddFile("BAD\x01X", new byte[1]));
  }

  [Test, Category("ErrorHandling")]
  public void Build_OversizeFile_Throws() {
    var w = new Mfs1Writer();
    Assert.Throws<ArgumentException>(() => w.AddFile("BIG", new byte[0x40000]));
  }

  [Test, Category("HappyPath")]
  public void Build_ZeroLengthFile_RoundTrips() {
    var img = new Mfs1Writer().AddFile("EMPTYF", System.Array.Empty<byte>()).Build();
    var r = new Mfs1Reader(img);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(0u));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(System.Array.Empty<byte>()));
  }
}
