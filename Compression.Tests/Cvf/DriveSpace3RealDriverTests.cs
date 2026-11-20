using System.Buffers.Binary;
using System.IO.Compression;
using Compression.Registry;
using Compression.Tests.Support;
using FileSystem.DriveSpace3;

namespace Compression.Tests.Cvf;

/// <summary>
/// DriveSpace 3 volumes against Windows 98 SE's own DriveSpace 3. The volumes it created — Normal (JM-0-0),
/// HiPack (JM-0-1) and, after Compression Agent, UltraPack (SQ-0-0) — must read byte-exact through our
/// reader and share our writer's system area; the gated tests boot the CD's real-mode DOS under qemu
/// (<see cref="MsDosGuest.Win98"/>) and let its DRVSPACE.BIN mount, read and SCANDISK check what our writer
/// creates.
/// </summary>
[TestFixture]
public sealed class DriveSpace3RealDriverTests {

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), $"cwb_ds3_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, true); } catch (IOException) { /* best effort */ }
  }

  private static (string Name, byte[] Data)[] Files() {
    var random = new byte[5000];
    new Random(13).NextBytes(random);
    return [("HELLO.TXT", "Hello from CompressionWorkbench\r\n"u8.ToArray()), ("RANDOM.BIN", random), ("TEXT.TXT", CvfSamples.Get("text-32k")[..20000])];
  }

  private static byte[] Create(string method) {
    using var ms = new MemoryStream();
    new DriveSpace3FormatDescriptor().Create(ms, [.. Files().Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data))], new FormatCreateOptions { MethodName = method });
    return ms.ToArray();
  }

  /// <summary>A volume Windows 98 wrote (ReferenceVectors/Volumes), and the method header of its TEXT.TXT cluster.</summary>
  public sealed record RealVolume(string Resource, string TextClusterHeader) {
    public byte[] Bytes() {
      using var resource = typeof(DriveSpace3RealDriverTests).Assembly.GetManifestResourceStream($"CvfVolumes.{this.Resource}")
        ?? throw new InvalidOperationException($"embedded volume {this.Resource} missing");
      using var gz = new GZipStream(resource, CompressionMode.Decompress);
      using var ms = new MemoryStream();
      gz.CopyTo(ms);
      return ms.ToArray();
    }

    public override string ToString() => this.Resource;
  }

  private static readonly RealVolume[] RealVolumes = [
    new("win98-drvspace3-normal.002.gz", "JM\0\0"),
    new("win98-drvspace3-hipack.003.gz", "JM\0\u0001"),
    new("win98-drvspace3-ultrapack.004.gz", "SQ\0\0"),
  ];

  private static void AssertReadsFiles(byte[] image, IEnumerable<(string Name, byte[] Data)> files) {
    using var reader = new GenuineDvr3Reader(new MemoryStream(image));
    Assert.Multiple(() => {
      foreach (var (name, data) in files) {
        var entry = reader.Entries.SingleOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Assert.That(entry, Is.Not.Null, $"{name} missing");
        if (entry is not null) Assert.That(reader.Extract(entry), Is.EqualTo(data), name);
      }
    });
  }

  // The first sector of the run an inner cluster's 5-byte MDFAT entry points at.
  private static int RunSector(byte[] image, int cluster) {
    var mdfat = (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x24)) + 1) * 512;
    var index = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x2D)) + cluster;
    var at = mdfat + index * 5 + index / 102 * 2;
    return (image[at] | image[at + 1] << 8 | image[at + 2] << 16) + 1;
  }

  // The 4-byte method header of that run (cluster 4 holds TEXT.TXT, the only compressible file).
  private static string ClusterHeader(byte[] image, int cluster)
    => new([.. image.AsSpan(RunSector(image, cluster) * 512, 4).ToArray().Select(b => (char)b)]);

  // ── The volumes Windows 98 wrote ─────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenARealVolume_WhenRead_ThenEveryFileIsByteExact([ValueSource(nameof(RealVolumes))] RealVolume volume)
    => AssertReadsFiles(volume.Bytes(), Files());

  [Test, Category("HappyPath")]
  public void GivenARealVolume_ThenItsTextClusterCarriesTheMethodItsSettingWrites([ValueSource(nameof(RealVolumes))] RealVolume volume)
    => Assert.That(ClusterHeader(volume.Bytes(), 4), Is.EqualTo(volume.TextClusterHeader));

  [Test, Category("HappyPath")]
  public void GivenARealVolume_WhenDetected_ThenItIsDriveSpace3([ValueSource(nameof(RealVolumes))] RealVolume volume)
    => Assert.That(new DriveSpace3FormatDescriptor().List(new MemoryStream(volume.Bytes()), null).Select(e => e.Name),
      Is.EquivalentTo(Files().Select(f => f.Name)));

  [Test, Category("HappyPath")]
  public void GivenOurVolume_ThenItsSystemAreaMatchesTheRealOne([Values("stored", "jm", "sq")] string method) {
    var real = RealVolumes[0].Bytes();
    var ours = Create(method);
    const int innerBase = 1693;

    Assert.Multiple(() => {
      Assert.That(ours[..0x48], Is.EqualTo(real[..0x48]), "MDBPB");
      Assert.That(ours[(innerBase * 512)..(innerBase * 512 + 0x26)], Is.EqualTo(real[(innerBase * 512)..(innerBase * 512 + 0x26)]), "inner boot sector");
      Assert.That(ours[((innerBase + 1) * 512)..((innerBase + 1) * 512 + 4)], Is.EqualTo(real[((innerBase + 1) * 512)..((innerBase + 1) * 512 + 4)]), "inner signature");
      Assert.That(ours[^512..^508], Is.EqualTo(real[^512..^508]), "MDR signature");
      Assert.That(RunSector(ours, 2), Is.EqualTo(RunSector(real, 2)), "the first run starts where the created volume's does");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenARealVolume_WhenAFileIsAdded_ThenOldAndNewFilesReadBack([ValueSource(nameof(RealVolumes))] RealVolume volume) {
    using var image = new MemoryStream();
    image.Write(volume.Bytes());
    var more = CvfSamples.Get("dir-8k");

    new DriveSpace3FormatDescriptor().Add(image, [ArchiveInputInfo.InMemory("MORE.DAT", more)]);

    AssertReadsFiles(image.ToArray(), [.. Files(), ("MORE.DAT", more)]);
  }

  [Test, Category("Sad")]
  public void GivenMoreFilesThanTheRootDirectoryHolds_WhenBuilt_ThenItIsRefused() {
    var w = new GenuineDvr3Writer();
    for (var i = 0; i < 513; ++i) w.AddFile($"F{i}.DAT", [1]);

    Assert.That(() => w.Build(), Throws.InstanceOf<NotSupportedException>());
  }

  // ── Windows 98's DRVSPACE.BIN on what our writer creates (opt-in, MsDosImageStaging.md) ──

  private const string ReadAndCheckBatch = """
    @ECHO OFF
    ECHO START> A:\LOG.TXT
    DIR /A C: >> A:\LOG.TXT
    MD A:\OUT
    COPY C:\*.* A:\OUT >> A:\LOG.TXT
    SCANDISK C: /CHECKONLY /NOSUMMARY
    IF ERRORLEVEL 1 ECHO SCANDISK-FOUND-PROBLEMS>> A:\LOG.TXT
    IF NOT ERRORLEVEL 1 ECHO SCANDISK-CLEAN>> A:\LOG.TXT
    ECHO DONE>> A:\LOG.TXT
    """;

  /// <summary>
  /// Windows 98's real-mode DRVSPACE.BIN mounts what the DriveSpace 3 descriptor creates at boot, reads every
  /// file byte-exact, and its SCANDISK finds nothing wrong.
  /// </summary>
  [Test, Category("DriverProof")]
  public void DriveSpace3_OurVolume_Win98MountsReadsAndChecksClean([Values("stored", "jm", "sq", "auto")] string method) {
    var run = MsDosGuest.Boot(MsDosGuest.Win98, this._tmp, Create(method), ReadAndCheckBatch);

    Assert.Multiple(() => {
      foreach (var (name, data) in Files()) Assert.That(run.Out(name), Is.EqualTo(data), $"{name}\n{run.Log}{run.Screen}");
      Assert.That(run.Log, Does.Contain("SCANDISK-CLEAN"), run.Log + run.Screen);
    });
  }
}
