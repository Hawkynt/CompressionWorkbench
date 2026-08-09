using System.Text;
using Compression.Registry;
using FileSystem.Nwfs;

namespace Compression.Tests.Nwfs;

/// <summary>
/// Volumes written by <see cref="NwfsWriter" />, read back the way a NetWare
/// reader reads one.
/// </summary>
/// <remarks>
/// <para>The route these follow is the one an outside reader takes: the
/// partition table to the hotfix header, the hotfix header to the volume area,
/// the volume area to the directory, and the FAT from a file's first block to
/// the rest of it. A volume that satisfies these is one the reverse-engineering
/// project's own tool lists and copies from — which is how the writer was
/// checked when it was written.</para>
/// </remarks>
[TestFixture]
public class NwfsVolumeTests {

  private static byte[] Bytes(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  [Test, Category("HappyPath")]
  public void AVolume_ReadsBackAsWhatWasPutOnIt() {
    var writer = new NwfsWriter();
    var hello = Bytes(12, 1);
    var readme = Bytes(5000, 2);
    var deep = Bytes(100, 3);
    writer.AddFile("HELLO.TXT", hello);
    writer.AddFile("PUBLIC/README.DOC", readme);
    writer.AddFile("PUBLIC/NESTED/DEEP.DAT", deep);

    var volume = NwfsReader.TryOpen(writer.Build());

    Assert.That(volume, Is.Not.Null);
    Assert.That(volume!.VolumeName, Is.EqualTo("SYS"));
    Assert.Multiple(() => {
      Assert.That(volume.ReadFile("HELLO.TXT"), Is.EqualTo(hello));
      Assert.That(volume.ReadFile("PUBLIC/README.DOC"), Is.EqualTo(readme));
      Assert.That(volume.ReadFile("PUBLIC/NESTED/DEEP.DAT"), Is.EqualTo(deep));
    });
  }

  [Test, Category("HappyPath")]
  public void TheDirectoriesLeadingToAFile_AreOnTheVolumeToo() {
    var writer = new NwfsWriter();
    writer.AddFile("PUBLIC/NESTED/DEEP.DAT", Bytes(10, 4));

    var items = NwfsReader.TryOpen(writer.Build())!.List();

    Assert.That(items.Where(i => i.IsDirectory).Select(i => i.Path),
                Is.EquivalentTo(new[] { "PUBLIC", "PUBLIC/NESTED" }));
  }

  [Test, Category("EdgeCase")]
  [TestCase(1024)]
  [TestCase(4096)]
  [TestCase(8192)]
  [TestCase(65536)]
  public void EveryBlockSizeNetWareNames_CarriesItsFiles(int blockSize) {
    var writer = new NwfsWriter { BlockSize = blockSize };
    var payload = Bytes(blockSize * 2 + 37, blockSize);
    writer.AddFile("SPAN.BIN", payload);

    var volume = NwfsReader.TryOpen(writer.Build());

    Assert.That(volume, Is.Not.Null);
    Assert.That(volume!.BlockSize, Is.EqualTo(blockSize));
    Assert.That(volume.ReadFile("SPAN.BIN"), Is.EqualTo(payload));
  }

  [Test, Category("EdgeCase")]
  public void AFileEndingExactlyOnABlock_DoesNotGainOne() {
    var writer = new NwfsWriter { BlockSize = 4096 };
    var payload = Bytes(4096, 5);
    writer.AddFile("EXACT.BIN", payload);

    Assert.That(NwfsReader.TryOpen(writer.Build())!.ReadFile("EXACT.BIN"), Is.EqualTo(payload));
  }

  [Test, Category("EdgeCase")]
  public void AnEmptyFile_IsOnTheVolumeAndHasNoBlocks() {
    var writer = new NwfsWriter();
    writer.AddFile("EMPTY.TXT", []);

    var volume = NwfsReader.TryOpen(writer.Build())!;

    Assert.That(volume.ReadFile("EMPTY.TXT"), Is.Empty);
    Assert.That(volume.List().Single(i => !i.IsDirectory).Length, Is.Zero);
  }

  [Test, Category("EdgeCase")]
  public void MoreFilesThanOneDirectoryBlockHolds_AreAllStillFound() {
    // A 1 KB block holds eight entries, so this needs a chain of them.
    var writer = new NwfsWriter { BlockSize = 1024 };
    for (var i = 0; i < 200; ++i) writer.AddFile($"F{i:D5}.BIN", Bytes(i % 900, i));

    var volume = NwfsReader.TryOpen(writer.Build())!;

    Assert.That(volume.List().Count(i => !i.IsDirectory), Is.EqualTo(200));
    Assert.That(volume.ReadFile("F00199.BIN"), Is.EqualTo(Bytes(199 % 900, 199)));
  }

  [Test, Category("HappyPath")]
  public void TheDirectoryIsWrittenTwice_AndTheCopySaysTheSame()  {
    var writer = new NwfsWriter { BlockSize = 4096 };
    writer.AddFile("HELLO.TXT", Bytes(20, 6));
    var image = writer.Build();

    // The volume entry names both copies; they are to hold the same bytes.
    var volumeArea = FindVolumeArea(image);
    var entry = image.AsSpan(volumeArea + 32);
    var first = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry[48..]);
    var copy = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry[52..]);
    var dataArea = volumeArea + 4 * 16384;

    Assert.That(copy, Is.Not.EqualTo(first));
    Assert.That(image.AsSpan(dataArea + (int)copy * 4096, 4096).SequenceEqual(
                  image.AsSpan(dataArea + (int)first * 4096, 4096)), Is.True);
  }

  [Test, Category("HappyPath")]
  public void ADiskWrittenHere_IsFoundByItsPartitionTable() {
    var writer = new NwfsWriter { PartitionStartSector = 2048 };
    writer.AddFile("HELLO.TXT", Bytes(9, 7));

    // Nothing sits at the offset a bare partition image would use, so the
    // volume can only have been reached through the partition table.
    var image = writer.Build();
    Assert.That(image.AsSpan(0x4000, 8).SequenceEqual("HOTFIX00"u8), Is.False);
    Assert.That(NwfsReader.TryOpen(image)!.ReadFile("HELLO.TXT"), Has.Length.EqualTo(9));
  }

  [Test, Category("ErrorHandling")]
  public void SomethingThatIsNotAVolume_IsNotReadAsOne() {
    Assert.That(NwfsReader.TryOpen(new byte[128 * 1024]), Is.Null);
    Assert.That(NwfsReader.TryOpen([]), Is.Null);
  }

  [Test, Category("ErrorHandling")]
  public void ANameLongerThanAnEntryHolds_IsRefusedRatherThanTruncated() {
    var writer = new NwfsWriter();
    writer.AddFile("THIRTEEN_CHARS.BIN", Bytes(4, 8));

    Assert.Throws<InvalidOperationException>(() => writer.Build());
  }

  [Test, Category("ErrorHandling")]
  public void ABlockSizeNetWareCannotName_IsRefused() {
    var writer = new NwfsWriter { BlockSize = 3000 };
    writer.AddFile("A.BIN", Bytes(4, 9));

    Assert.Throws<InvalidOperationException>(() => writer.Build());
  }

  [Test, Category("HappyPath")]
  public void TheDescriptor_ListsTheFilesOnAVolumeItIsGiven() {
    var writer = new NwfsWriter();
    writer.AddFile("HELLO.TXT", Bytes(12, 10));
    writer.AddFile("PUBLIC/README.DOC", Bytes(40, 11));
    using var stream = new MemoryStream(writer.Build());

    var names = new NwfsFormatDescriptor().List(stream, null).Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("HELLO.TXT"));
    Assert.That(names, Does.Contain("PUBLIC/README.DOC"));
    Assert.That(names, Does.Contain("PUBLIC"));
  }

  [Test, Category("HappyPath")]
  public void TheDescriptor_ExtractsTheBytesThatWerePutOnTheVolume() {
    var payload = Bytes(3000, 12);
    var writer = new NwfsWriter();
    writer.AddFile("PUBLIC/README.DOC", payload);
    using var stream = new MemoryStream(writer.Build());

    var outDir = Path.Combine(Path.GetTempPath(), "nwfs-" + Guid.NewGuid().ToString("N"));
    try {
      new NwfsFormatDescriptor().Extract(stream, outDir, null, null);

      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "PUBLIC", "README.DOC")), Is.EqualTo(payload));
      Assert.That(File.ReadAllText(Path.Combine(outDir, "metadata.ini")),
                  Does.Contain("parse_status=ok").And.Contain("volume_name=SYS"));
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    }
  }

  private static int FindVolumeArea(byte[] image) {
    var wanted = "NetWare Volumes\0"u8;
    for (var i = 0; i + wanted.Length <= image.Length; i += 512)
      if (image.AsSpan(i, wanted.Length).SequenceEqual(wanted))
        return i;
    throw new InvalidOperationException("no volume area");
  }
}
