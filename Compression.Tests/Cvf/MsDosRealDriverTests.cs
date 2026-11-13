using System.IO.Compression;
using Compression.Core.FileSystems.Cvf;
using Compression.Registry;
using Compression.Tests.Support;
using FileSystem.DoubleSpace;

namespace Compression.Tests.Cvf;

/// <summary>
/// DoubleSpace / DriveSpace volumes against the real Microsoft drivers. The checked-in volume MS-DOS 6.22
/// created (see ReferenceVectors/Volumes/README.md) must read byte-exact through our readers and share
/// our writer's system-area geometry; the gated tests boot the owner's MS-DOS 6.22 and 7.1 under qemu
/// (<see cref="MsDosGuest"/>) and let their drivers mount, read, write and check what our writer creates.
/// </summary>
[TestFixture]
public sealed class MsDosRealDriverTests {

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), $"cwb_msdos_{Guid.NewGuid():N}");
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

  private static byte[] Create(IArchiveCreatable descriptor, string method) {
    using var ms = new MemoryStream();
    descriptor.Create(ms, [.. Files().Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data))], new FormatCreateOptions { MethodName = method });
    return ms.ToArray();
  }

  private static byte[] Dos622Volume() {
    using var resource = typeof(MsDosRealDriverTests).Assembly.GetManifestResourceStream("CvfVolumes.msdos622-drvspace.001.gz")
      ?? throw new InvalidOperationException("embedded MS-DOS 6.22 volume missing");
    using var gz = new GZipStream(resource, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private static void AssertReadsFiles(byte[] image, IEnumerable<(string Name, byte[] Data)> files) {
    using var reader = new GenuineCvfReader(new MemoryStream(image));
    Assert.Multiple(() => {
      foreach (var (name, data) in files) {
        var entry = reader.Entries.SingleOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Assert.That(entry, Is.Not.Null, $"{name} missing");
        if (entry is not null) Assert.That(reader.Extract(entry), Is.EqualTo(data), name);
      }
    });
  }

  // ── The volume MS-DOS 6.22 wrote ─────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenTheMsDos622Volume_WhenRead_ThenEveryFileIsByteExact() {
    var image = Dos622Volume();

    AssertReadsFiles(image, Files());
    var general = new DoubleSpaceReader(new MemoryStream(image));
    Assert.Multiple(() => {
      foreach (var (name, data) in Files())
        Assert.That(general.Extract(general.Entries.Single(e => e.Name.EndsWith(name, StringComparison.OrdinalIgnoreCase))), Is.EqualTo(data), name);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheMsDos622Volume_ThenItsClustersAreJm()
    => Assert.That(GenuineCvfVolume.CodecInUse(Dos622Volume()), Is.EqualTo(CvfLzMethod.Jm));

  [Test, Category("HappyPath")]
  public void GivenOurDriveSpaceVolume_ThenItsSystemAreaMatchesTheMsDos622One() {
    var real = Dos622Volume();
    var ours = Create(new DriveSpaceFormatDescriptor(), "jm");
    const int innerBase = 429;

    Assert.Multiple(() => {
      // MDBPB: everything but the total-sector count (0x20), which follows the content.
      Assert.That(ours[..0x20], Is.EqualTo(real[..0x20]), "MDBPB 0x00-0x1F");
      Assert.That(ours[0x24..0x40], Is.EqualTo(real[0x24..0x40]), "MDBPB 0x24-0x3F");
      // Inner boot sector BPB up to the hidden-sector count.
      Assert.That(ours[(innerBase * 512 + 0x0B)..(innerBase * 512 + 0x1C)], Is.EqualTo(real[(innerBase * 512 + 0x0B)..(innerBase * 512 + 0x1C)]), "inner BPB");
      Assert.That(ours[((innerBase + 1) * 512)..((innerBase + 1) * 512 + 4)], Is.EqualTo(real[((innerBase + 1) * 512)..((innerBase + 1) * 512 + 4)]), "inner signature");
      Assert.That(ours[^512..^508], Is.EqualTo(real[^512..^508]), "MDR signature");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheMsDos622Volume_WhenAFileIsAdded_ThenOldAndNewFilesReadBack() {
    using var image = new MemoryStream();
    image.Write(Dos622Volume());
    var more = CvfSamples.Get("dir-8k");

    new DriveSpaceFormatDescriptor().Add(image, [ArchiveInputInfo.InMemory("MORE.DAT", more)]);

    AssertReadsFiles(image.ToArray(), [.. Files(), ("MORE.DAT", more)]);
    Assert.That(GenuineCvfVolume.CodecInUse(image.ToArray()), Is.EqualTo(CvfLzMethod.Jm));
  }

  [Test, Category("Sad")]
  public void GivenMoreClustersThanFat12Holds_WhenBuilt_ThenItIsRefused() {
    var w = new GenuineCvfWriter();
    w.AddFile("BIG.DAT", new byte[4081 * 8192]);

    Assert.That(() => w.Build(), Throws.InstanceOf<NotSupportedException>());
  }

  [Test, Category("Boundary")]
  public void GivenTheMostClustersFat12Holds_WhenBuilt_ThenItReadsBack() {
    var big = new byte[4080 * 8192];
    new Random(3).NextBytes(big.AsSpan(0, 65536));
    big[^1] = 0x5A;
    var w = new GenuineCvfWriter();
    w.AddFile("BIG.DAT", big);

    AssertReadsFiles(w.Build(), [("BIG.DAT", big)]);
  }

  [Test, Category("Sad")]
  public void GivenMoreFilesThanTheRootDirectoryHolds_WhenBuilt_ThenItIsRefused() {
    var w = new GenuineCvfWriter();
    for (var i = 0; i < 513; ++i) w.AddFile($"F{i}.DAT", [1]);

    Assert.That(() => w.Build(), Throws.InstanceOf<NotSupportedException>());
  }

  // ── Real drivers on what our writer creates (opt-in, MsDosImageStaging.md) ──

  private const string Dos622MountBatch = """
    @ECHO OFF
    ECHO START> A:\LOG.TXT
    DRVSPACE /MOUNT=000 C: /NEWDRIVE=E: >> A:\LOG.TXT
    DIR /A C: >> A:\LOG.TXT
    MD A:\OUT
    COPY C:\*.* A:\OUT >> A:\LOG.TXT
    COPY A:\IN\NEW.TXT C:\NEW.TXT >> A:\LOG.TXT
    COPY C:\NEW.TXT A:\OUT\NEW.TXT >> A:\LOG.TXT
    SCANDISK C: /CUSTOM
    IF ERRORLEVEL 1 ECHO SCANDISK-FOUND-PROBLEMS>> A:\LOG.TXT
    IF NOT ERRORLEVEL 1 ECHO SCANDISK-CLEAN>> A:\LOG.TXT
    ECHO DONE>> A:\LOG.TXT
    """;

  private const string Dos71ReadBatch = """
    @ECHO OFF
    ECHO START> A:\LOG.TXT
    DIR /A C: >> A:\LOG.TXT
    MD A:\OUT
    COPY C:\*.* A:\OUT >> A:\LOG.TXT
    ECHO DONE>> A:\LOG.TXT
    """;

  private const string Dos622CreateBatch = """
    @ECHO OFF
    ECHO START> A:\LOG.TXT
    DRVSPACE /CREATE C: /NEWDRIVE=E: /SIZE=0.5 >> A:\LOG.TXT
    COPY A:\IN\*.* E:\ >> A:\LOG.TXT
    SCANDISK E: /CUSTOM
    IF ERRORLEVEL 1 ECHO SCANDISK-FOUND-PROBLEMS>> A:\LOG.TXT
    IF NOT ERRORLEVEL 1 ECHO SCANDISK-CLEAN>> A:\LOG.TXT
    ECHO DONE>> A:\LOG.TXT
    """;

  /// <summary>
  /// MS-DOS 6.22's DRVSPACE mounts what the DriveSpace descriptor creates, reads every file byte-exact,
  /// writes a new file into it that reads back, and SCANDISK finds nothing to fix; our reader then reads
  /// the volume the driver left, the driver's own file included.
  /// </summary>
  [Test, Category("DriverProof")]
  public void DriveSpace_OurVolume_MsDos622MountsReadsWritesAndChecksClean([Values("jm", "stored")] string method) {
    var newFile = "Written by the MS-DOS 6.22 driver\r\n"u8.ToArray();
    var run = MsDosGuest.Boot622(this._tmp, Create(new DriveSpaceFormatDescriptor(), method), Dos622MountBatch, [("NEW.TXT", newFile)]);

    Assert.Multiple(() => {
      Assert.That(run.Log, Does.Contain("has mounted drive C"), run.Log + run.Screen);
      foreach (var (name, data) in Files()) Assert.That(run.Out(name), Is.EqualTo(data), name);
      Assert.That(run.Out("NEW.TXT"), Is.EqualTo(newFile), "driver write read back");
      Assert.That(run.Log, Does.Contain("SCANDISK-CLEAN"), run.Log + run.Screen);
    });
    var after = run.Volume("DRVSPACE.000");
    Assert.That(after, Is.Not.Null);
    AssertReadsFiles(after!, [.. Files(), ("NEW.TXT", newFile)]);
  }

  /// <summary>MS-DOS 7.1's DRVSPACE.BIN mounts what the DoubleSpace and DriveSpace descriptors create and reads every file byte-exact.</summary>
  [Test, Category("DriverProof")]
  public void DoubleSpaceAndDriveSpace_OurVolume_MsDos71MountsAndReads(
      [Values("doublespace:ds", "doublespace:stored", "drivespace:jm")] string productAndMethod) {
    var (product, method) = (productAndMethod.Split(':')[0], productAndMethod.Split(':')[1]);
    IArchiveCreatable descriptor = product == "doublespace" ? new DoubleSpaceFormatDescriptor() : new DriveSpaceFormatDescriptor();

    var run = MsDosGuest.Boot71(this._tmp, Create(descriptor, method), Dos71ReadBatch);

    Assert.Multiple(() => {
      foreach (var (name, data) in Files()) Assert.That(run.Out(name), Is.EqualTo(data), $"{name}\n{run.Log}{run.Screen}");
    });
  }

  /// <summary>MS-DOS 6.22's DRVSPACE /CREATE writes a volume our reader reads byte-exact (the live form of the checked-in fixture).</summary>
  [Test, Category("DriverProof")]
  public void DriveSpace_MsDos622CreatedVolume_OurReaderReads() {
    var run = MsDosGuest.Boot622(this._tmp, null, Dos622CreateBatch, Files());

    Assert.That(run.Log, Does.Contain("SCANDISK-CLEAN"), run.Log + run.Screen);
    var created = run.Volume("DRVSPACE.001");
    Assert.That(created, Is.Not.Null, run.Log);
    AssertReadsFiles(created!, Files());
  }
}
