using System.IO.Compression;
using Compression.Core.FileSystems.Cvf;
using Compression.Registry;
using Compression.Tests.Support;
using FileSystem.DoubleSpace;
using FileSystem.DriveSpace3;

namespace Compression.Tests.Cvf;

/// <summary>
/// DoubleSpace / DriveSpace volumes against the real Microsoft drivers. The checked-in volumes MS-DOS 6.0
/// and 6.22 created (see ReferenceVectors/Volumes/README.md) must read byte-exact through our readers and
/// share our writer's system area; the gated tests boot the owner's MS-DOS 6.0, 6.22 (English and German)
/// and 7.1 under qemu (<see cref="MsDosGuest"/>) and let their drivers mount, read, write and check what
/// our writer creates.
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

  /// <summary>A volume a real driver wrote (ReferenceVectors/Volumes), its codec and the descriptor of its product.</summary>
  public sealed record RealVolume(string Resource, CvfLzMethod Codec, string Product) {
    public byte[] Bytes() {
      using var resource = typeof(MsDosRealDriverTests).Assembly.GetManifestResourceStream($"CvfVolumes.{this.Resource}")
        ?? throw new InvalidOperationException($"embedded volume {this.Resource} missing");
      using var gz = new GZipStream(resource, CompressionMode.Decompress);
      using var ms = new MemoryStream();
      gz.CopyTo(ms);
      return ms.ToArray();
    }

    public IArchiveCreatable Descriptor() => this.Product == "DoubleSpace" ? new DoubleSpaceFormatDescriptor() : new DriveSpaceFormatDescriptor();

    public string OurMethod => this.Codec == CvfLzMethod.Ds ? "ds" : "jm";

    public override string ToString() => this.Resource;
  }

  private static readonly RealVolume[] RealVolumes = [
    new("msdos622-drvspace.001.gz", CvfLzMethod.Jm, "DriveSpace"),
    new("msdos60-dblspace.001.gz", CvfLzMethod.Ds, "DoubleSpace"),
  ];

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

  // ── The volumes MS-DOS 6.0 and 6.22 wrote ───────────────────────────

  [Test, Category("HappyPath")]
  public void GivenARealVolume_WhenRead_ThenEveryFileIsByteExact([ValueSource(nameof(RealVolumes))] RealVolume volume) {
    var image = volume.Bytes();

    AssertReadsFiles(image, Files());
    var general = new DoubleSpaceReader(new MemoryStream(image));
    Assert.Multiple(() => {
      foreach (var (name, data) in Files())
        Assert.That(general.Extract(general.Entries.Single(e => e.Name.EndsWith(name, StringComparison.OrdinalIgnoreCase))), Is.EqualTo(data), name);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenARealVolume_ThenItsClustersUseItsProductsCodec([ValueSource(nameof(RealVolumes))] RealVolume volume)
    => Assert.That(GenuineCvfVolume.CodecInUse(volume.Bytes()), Is.EqualTo(volume.Codec));

  [Test, Category("HappyPath")]
  public void GivenOurVolumeOfTheSameProduct_ThenItsSystemAreaMatchesTheRealOne([ValueSource(nameof(RealVolumes))] RealVolume volume) {
    var real = volume.Bytes();
    var ours = Create(volume.Descriptor(), volume.OurMethod);
    const int innerBase = 429;

    Assert.Multiple(() => {
      // MDBPB: everything but the total-sector count (0x20), which follows the content.
      Assert.That(ours[..0x20], Is.EqualTo(real[..0x20]), "MDBPB 0x00-0x1F");
      Assert.That(ours[0x24..0x40], Is.EqualTo(real[0x24..0x40]), "MDBPB 0x24-0x3F");
      // Inner boot sector: OEM name and BPB up to the hidden-sector count.
      Assert.That(ours[(innerBase * 512 + 0x03)..(innerBase * 512 + 0x1C)], Is.EqualTo(real[(innerBase * 512 + 0x03)..(innerBase * 512 + 0x1C)]), "inner BPB");
      Assert.That(ours[((innerBase + 1) * 512)..((innerBase + 1) * 512 + 4)], Is.EqualTo(real[((innerBase + 1) * 512)..((innerBase + 1) * 512 + 4)]), "inner signature");
      Assert.That(ours[^512..^508], Is.EqualTo(real[^512..^508]), "MDR signature");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenARealVolume_WhenAFileIsAdded_ThenOldAndNewFilesReadBackInTheSameContainer([ValueSource(nameof(RealVolumes))] RealVolume volume) {
    using var image = new MemoryStream();
    var real = volume.Bytes();
    image.Write(real);
    var more = CvfSamples.Get("dir-8k");

    ((IArchiveModifiable)volume.Descriptor()).Add(image, [ArchiveInputInfo.InMemory("MORE.DAT", more)]);

    var after = image.ToArray();
    AssertReadsFiles(after, [.. Files(), ("MORE.DAT", more)]);
    Assert.Multiple(() => {
      Assert.That(GenuineCvfVolume.CodecInUse(after), Is.EqualTo(volume.Codec));
      Assert.That(after[3..11], Is.EqualTo(real[3..11]), "OEM name kept");
      Assert.That(after[0x33], Is.EqualTo(real[0x33]), "CVF version kept");
    });
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

  /// <summary>Mount C:\&lt;product&gt;.000 as C:, read it, write into it, then check it.</summary>
  private static string MountBatch(string check) => $"""
    @ECHO OFF
    ECHO START> A:\LOG.TXT
    %TOOL% /MOUNT=000 C: /NEWDRIVE=E: >> A:\LOG.TXT
    DIR /A C: >> A:\LOG.TXT
    MD A:\OUT
    COPY C:\*.* A:\OUT >> A:\LOG.TXT
    COPY A:\IN\NEW.TXT C:\NEW.TXT >> A:\LOG.TXT
    COPY C:\NEW.TXT A:\OUT\NEW.TXT >> A:\LOG.TXT
    {check}
    ECHO DONE>> A:\LOG.TXT
    """;

  /// <summary>Create a 0.5 MB volume from the host's free space, fill it, check it.</summary>
  private static string CreateBatch(string check) => $"""
    @ECHO OFF
    ECHO START> A:\LOG.TXT
    %TOOL% /CREATE C: /NEWDRIVE=E: /SIZE=0.5 >> A:\LOG.TXT
    COPY A:\IN\*.* E:\ >> A:\LOG.TXT
    {check}
    ECHO DONE>> A:\LOG.TXT
    """;

  // DriveSpace 6.22 checks with SCANDISK (its exit code says whether it found anything); DoubleSpace 6.0
  // with DBLSPACE /CHKDSK, whose verdict is only in its text.
  private static string ScanDisk(string drive) => $"""
    SCANDISK {drive} /CUSTOM
    IF ERRORLEVEL 1 ECHO SCANDISK-FOUND-PROBLEMS>> A:\LOG.TXT
    IF NOT ERRORLEVEL 1 ECHO SCANDISK-CLEAN>> A:\LOG.TXT
    """;

  private static string ChkDsk(string drive) => $"%TOOL% /CHKDSK {drive} >> A:\\LOG.TXT";

  private static readonly string[] ChkDskClean = ["DoubleSpace found no errors", "keine Fehler"];

  private const string Dos71ReadBatch = """
    @ECHO OFF
    ECHO START> A:\LOG.TXT
    DIR /A C: >> A:\LOG.TXT
    MD A:\OUT
    COPY C:\*.* A:\OUT >> A:\LOG.TXT
    ECHO DONE>> A:\LOG.TXT
    """;

  // The English and German sets of each DOS.
  private static MsDosGuest.DosSystem DriveSpaceDos(string language) => language == "de" ? MsDosGuest.Dos622De : MsDosGuest.Dos622;

  private static MsDosGuest.DosSystem DoubleSpaceDos(string language) => language == "de" ? MsDosGuest.Dos60De : MsDosGuest.Dos60;

  /// <summary>
  /// MS-DOS 6.22's DRVSPACE mounts what the DriveSpace descriptor creates, reads every file byte-exact,
  /// writes a new file into it that reads back, and SCANDISK finds nothing to fix; our reader then reads
  /// the volume the driver left, the driver's own file included.
  /// </summary>
  [Test, Category("DriverProof")]
  public void DriveSpace_OurVolume_MsDos622MountsReadsWritesAndChecksClean(
      [Values("en", "de")] string language, [Values("jm", "stored")] string method) {
    var newFile = "Written by the MS-DOS 6.22 driver\r\n"u8.ToArray();
    var run = MsDosGuest.Boot(DriveSpaceDos(language), this._tmp, Create(new DriveSpaceFormatDescriptor(), method), MountBatch(ScanDisk("C:")), [("NEW.TXT", newFile)]);

    AssertMountedReadWritten(run, newFile, "DRVSPACE.000");
    Assert.That(run.Log, Does.Contain("SCANDISK-CLEAN"), run.Log + run.Screen);
  }

  /// <summary>
  /// MS-DOS 6.0's DBLSPACE mounts what the DoubleSpace descriptor creates, reads every file byte-exact,
  /// writes a new file into it that reads back, and DBLSPACE /CHKDSK finds no errors.
  /// </summary>
  [Test, Category("DriverProof")]
  public void DoubleSpace_OurVolume_MsDos60MountsReadsWritesAndChecksClean(
      [Values("en", "de")] string language, [Values("ds", "stored")] string method) {
    var newFile = "Written by the MS-DOS 6.0 driver\r\n"u8.ToArray();
    var run = MsDosGuest.Boot(DoubleSpaceDos(language), this._tmp, Create(new DoubleSpaceFormatDescriptor(), method), MountBatch(ChkDsk("C:")), [("NEW.TXT", newFile)]);

    AssertMountedReadWritten(run, newFile, "DBLSPACE.000");
    Assert.That(ChkDskClean.Any(run.Log.Contains), run.Log + run.Screen);
  }

  private static void AssertMountedReadWritten(MsDosGuest.Run run, byte[] newFile, string volumeName) {
    Assert.Multiple(() => {
      foreach (var (name, data) in Files()) Assert.That(run.Out(name), Is.EqualTo(data), $"{name}\n{run.Log}{run.Screen}");
      Assert.That(run.Out("NEW.TXT"), Is.EqualTo(newFile), "driver write read back");
    });
    var after = run.Volume(volumeName);
    Assert.That(after, Is.Not.Null);
    AssertReadsFiles(after!, [.. Files(), ("NEW.TXT", newFile)]);
  }

  /// <summary>MS-DOS 7.1's DRVSPACE.BIN mounts what the DoubleSpace, DriveSpace and DriveSpace 3 descriptors create and reads every file byte-exact.</summary>
  [Test, Category("DriverProof")]
  public void DoubleSpaceAndDriveSpace_OurVolume_MsDos71MountsAndReads(
      [Values("doublespace:ds", "doublespace:stored", "drivespace:jm", "drivespace3:jm", "drivespace3:sq")] string productAndMethod) {
    var (product, method) = (productAndMethod.Split(':')[0], productAndMethod.Split(':')[1]);
    IArchiveCreatable descriptor = product switch {
      "doublespace" => new DoubleSpaceFormatDescriptor(),
      "drivespace" => new DriveSpaceFormatDescriptor(),
      _ => new DriveSpace3FormatDescriptor(),
    };

    var run = MsDosGuest.Boot(MsDosGuest.Dos71, this._tmp, Create(descriptor, method), Dos71ReadBatch);

    Assert.Multiple(() => {
      foreach (var (name, data) in Files()) Assert.That(run.Out(name), Is.EqualTo(data), $"{name}\n{run.Log}{run.Screen}");
    });
  }

  /// <summary>MS-DOS 6.22's DRVSPACE /CREATE writes a volume our reader reads byte-exact (the live form of the checked-in fixture).</summary>
  [Test, Category("DriverProof")]
  public void DriveSpace_MsDos622CreatedVolume_OurReaderReads([Values("en", "de")] string language) {
    var run = MsDosGuest.Boot(DriveSpaceDos(language), this._tmp, null, CreateBatch(ScanDisk("E:")), Files());

    Assert.That(run.Log, Does.Contain("SCANDISK-CLEAN"), run.Log + run.Screen);
    var created = run.Volume("DRVSPACE.001");
    Assert.That(created, Is.Not.Null, run.Log);
    AssertReadsFiles(created!, Files());
  }

  /// <summary>MS-DOS 6.0's DBLSPACE /CREATE writes a volume our reader reads byte-exact.</summary>
  [Test, Category("DriverProof")]
  public void DoubleSpace_MsDos60CreatedVolume_OurReaderReads([Values("en", "de")] string language) {
    var run = MsDosGuest.Boot(DoubleSpaceDos(language), this._tmp, null, CreateBatch(ChkDsk("E:")), Files());

    Assert.That(ChkDskClean.Any(run.Log.Contains), run.Log + run.Screen);
    var created = run.Volume("DBLSPACE.001");
    Assert.That(created, Is.Not.Null, run.Log);
    AssertReadsFiles(created!, Files());
  }
}
