using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using Compression.Core.DiskImage;

namespace Compression.Tests.DiskImage;

/// <summary>
/// The Atari AHDI partition table against a disk GNU parted 3.6 labelled <c>atari</c> (two
/// primaries, an XGM chain with three logicals, FAT filesystems in two of them; see
/// AhdiVectors/README.md), checked against what util-linux <c>partx</c> lists (partx.txt).
/// </summary>
[TestFixture]
public sealed class AtariAhdiParserTests {

  private static Stream Resource(string name) =>
    Assembly.GetExecutingAssembly().GetManifestResourceStream("AhdiVectors." + name)
    ?? throw new FileNotFoundException(name);

  private static byte[] Disk() {
    using var gz = new GZipStream(Resource("ahdi.img.gz"), CompressionMode.Decompress); // BCL as the test-side oracle
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private static List<(long Start, long Size)> Partx() {
    using var reader = new StreamReader(Resource("partx.txt"));
    return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Where(l => !l.StartsWith('#'))
      .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
      .Select(f => (long.Parse(f[1], CultureInfo.InvariantCulture) * 512, long.Parse(f[2], CultureInfo.InvariantCulture) * 512))
      .ToList();
  }

  [Test]
  public void GivenPartedAtariDisk_WhenDetected_ThenEveryPartitionSitsWherePartxPutsIt() {
    var detection = PartitionTableDetector.Detect(Disk());

    Assert.That(detection.Scheme, Is.EqualTo("AHDI"));
    Assert.That(detection.Partitions.Select(p => (p.StartOffset, p.Size)), Is.EqualTo(Partx()));
    Assert.That(detection.Partitions.Count(p => p.Source == "XGM"), Is.EqualTo(3));
    Assert.That(detection.Partitions.Select(p => p.TypeCode), Has.All.EqualTo("RAW").Or.EqualTo("BGM").Or.EqualTo("GEM"));
  }

  [Test]
  public void GivenPartedAtariDisk_WhenListed_ThenFilesInsidePrimaryAndLogicalPartitionsAppear() {
    var path = Path.Combine(Path.GetTempPath(), "cwb-ahdi-" + Guid.NewGuid().ToString("N") + ".img");
    try {
      File.WriteAllBytes(path, Disk());
      Compression.Lib.FormatRegistration.EnsureInitialized();

      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("PartitionedDisk"));
      var names = Compression.Lib.ArchiveOperations.List(path, null).Select(e => e.Name).ToList();
      Assert.That(names, Has.Some.Match("^Partition1_.*/HELLO\\.TXT$"));
      Assert.That(names, Has.Some.Match("^Partition4_.*/LOGICAL\\.TXT$"));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void GivenARecordReachingPastTheDiskSize_WhenProbed_ThenItIsNotAhdi() {
    var sector = Disk().AsSpan(0, 512).ToArray();
    BinaryPrimitives.WriteUInt32BigEndian(sector.AsSpan(0x1C6 + 8), 0x7FFFFFFF);
    Assert.That(AtariAhdiParser.IsAhdi(sector, 64L << 20), Is.False);
  }

  [Test]
  public void GivenADiskSizeLargerThanTheImage_WhenProbed_ThenItIsNotAhdi() {
    var sector = Disk().AsSpan(0, 512).ToArray();
    Assert.That(AtariAhdiParser.IsAhdi(sector, 32L << 20), Is.False);
  }

  [Test]
  public void GivenAnIdThatIsNotAlphanumeric_WhenProbed_ThenItIsNotAhdi() {
    var sector = Disk().AsSpan(0, 512).ToArray();
    sector[0x1C6 + 1] = 0x01;
    Assert.That(AtariAhdiParser.IsAhdi(sector, 64L << 20), Is.False);
  }

  [Test]
  public void GivenAnUnknownButAlphanumericId_WhenProbedByContent_ThenOnlyTheStrictTestRejectsIt() {
    var sector = Disk().AsSpan(0, 512).ToArray();
    "QQQ"u8.CopyTo(sector.AsSpan(0x1C6 + 1));
    Assert.That(AtariAhdiParser.IsAhdi(sector, 64L << 20), Is.True);
    Assert.That(AtariAhdiParser.IsKnownAhdi(sector, 64L << 20), Is.False);
  }

  [Test]
  public void GivenAnXgmChainThatLinksToItself_WhenParsed_ThenItIsRefused() {
    var disk = Disk();
    // The first auxiliary root sector (at the XGM start, sector 20480) links to the next link at
    // offset 0 from the first XGM start: itself.
    var aux = 20480 * 512;
    BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(aux + 0x1C6 + 12 + 4), 0);
    Assert.Throws<InvalidDataException>(() => AtariAhdiParser.Parse(new MemoryStream(disk)));
  }

  [Test]
  public void GivenAnEmptySector_WhenProbed_ThenItIsNotAhdi()
    => Assert.That(AtariAhdiParser.IsAhdi(new byte[512], 1 << 20), Is.False);
}
