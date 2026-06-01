using System.Text;
using Compression.Registry;
using FileSystem.CbmNibble;
using FileSystem.D64;

namespace Compression.Tests.CbmNibble;

[TestFixture]
public class CbmNibbleWriterTests {

  // Given files written into a fresh nibble image, reading it back through the
  // existing reader and decoding the GCR tracks must recover names + content.
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Build_ThenRead_RecoversFileNamesAndContent() {
    var hello = Encoding.ASCII.GetBytes("HELLO WORLD FROM THE 1541");
    var loader = new byte[600];
    for (var i = 0; i < loader.Length; i++) loader[i] = (byte)(i * 7 + 3);

    var writer = new CbmNibbleWriter();
    writer.SetDisk("MY DISK", '4', '2');
    writer.AddFile("HELLO", hello);
    writer.AddFile("LOADER", loader);
    var image = writer.Build();

    // Read back through the production reader — it surfaces raw GCR tracks.
    var nibble = CbmNibbleReader.Read(image, "image.g64");
    Assert.That(nibble.Kind, Is.EqualTo(CbmNibbleReader.ImageKind.G64));

    // Decode the GCR tracks back to a sectored D64 and walk the directory.
    var d64 = CbmNibbleWriter.DecodeToD64(nibble);
    using var reader = new D64Reader(new MemoryStream(d64));

    var names = reader.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("HELLO"));
    Assert.That(names, Does.Contain("LOADER"));

    var helloEntry = reader.Entries.First(e => e.Name == "HELLO");
    var loaderEntry = reader.Entries.First(e => e.Name == "LOADER");
    Assert.That(reader.Extract(helloEntry), Is.EqualTo(hello));
    Assert.That(reader.Extract(loaderEntry), Is.EqualTo(loader));
  }

  // The G64 the writer emits must satisfy the reader's structural expectations.
  [Test, Category("HappyPath")]
  public void Build_ProducesParseableG64WithPopulatedTracks() {
    var writer = new CbmNibbleWriter();
    writer.AddFile("DATA", [1, 2, 3, 4, 5]);
    var image = writer.Build();

    var nibble = CbmNibbleReader.Read(image, "image.g64");
    Assert.That(nibble.Kind, Is.EqualTo(CbmNibbleReader.ImageKind.G64));
    // Track 18 (directory) is half-track index 34 and must carry GCR data.
    Assert.That(nibble.Tracks[34].Data, Is.Not.Empty);
    // Odd half-tracks between real tracks stay empty.
    Assert.That(nibble.Tracks[1].Data, Is.Empty);
  }

  // Long names get truncated to the 16-char Commodore limit.
  [Test, Category("EdgeCase")]
  public void Build_TruncatesNamesToSixteenCharacters() {
    var writer = new CbmNibbleWriter();
    writer.AddFile("THIS NAME IS WAY TOO LONG FOR CBM", [9, 9, 9]);
    var image = writer.Build();

    var d64 = CbmNibbleWriter.DecodeToD64(CbmNibbleReader.Read(image, "image.g64"));
    using var reader = new D64Reader(new MemoryStream(d64));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Name, Has.Length.EqualTo(16));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("THIS NAME IS WAY"));
  }

  // The descriptor exposes the create capability and round-trips through Create.
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsThroughReader() {
    var content = Encoding.ASCII.GetBytes("CREATED VIA DESCRIPTOR");
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("sub/dir/PROG", content),
    };

    var descriptor = new G64FormatDescriptor();
    Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);

    using var output = new MemoryStream();
    descriptor.Create(output, inputs, new FormatCreateOptions());

    var d64 = CbmNibbleWriter.DecodeToD64(CbmNibbleReader.Read(output.ToArray(), "image.g64"));
    using var reader = new D64Reader(new MemoryStream(d64));
    // Flat filesystem: nested path was reduced to its filename.
    Assert.That(reader.Entries.Select(e => e.Name), Does.Contain("PROG"));
    Assert.That(reader.Extract(reader.Entries.First(e => e.Name == "PROG")), Is.EqualTo(content));
  }
}
