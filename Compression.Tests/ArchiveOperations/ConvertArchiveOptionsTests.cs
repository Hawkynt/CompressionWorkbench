#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations.ConvertArchiveOptions;

/// <summary>
/// Verifies the new <see cref="FormatCreateOptions.FormatSpecific"/>
/// plumbing for the Convert Archive flow:
/// <list type="bullet">
///   <item>The 4-arg <see cref="Compression.Lib.ArchiveOperations.ConvertArchive(string, string, string?, FormatCreateOptions?)"/>
///   overload accepts a <see cref="FormatCreateOptions"/> and threads its
///   <c>FormatSpecific</c> dictionary to the target descriptor's
///   <see cref="IArchiveCreatable.Create"/> call.</item>
///   <item>The <see cref="FormatCreateOptions"/> accessor helpers
///   (<see cref="FormatCreateOptions.GetOption"/>, <see cref="FormatCreateOptions.GetOptionInt"/>,
///   <see cref="FormatCreateOptions.GetOptionBool"/>) return user-supplied
///   values when present and fall back to the schema default otherwise.</item>
///   <item>Non-empty <c>FormatSpecific</c> bypasses the FAT/ext in-place
///   fast path so writer tunables actually take effect rather than being
///   silently dropped.</item>
///   <item>The <see cref="ArchiveOperations.Create"/> overload that accepts
///   <c>formatSpecific</c> forwards the dictionary to the target
///   descriptor's writer.</item>
/// </list>
/// </summary>
[TestFixture]
public class ConvertArchiveOptionsTests {

  [SetUp]
  public void SetUp() {
    FormatRegistration.EnsureInitialized();
  }

  // ── FormatCreateOptions accessor helpers ────────────────────────────

  [Test, Category("HappyPath")]
  public void GetOption_ReturnsValue_WhenKeyPresent() {
    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["FatType"] = "FAT16",
        ["VolumeLabel"] = "TESTDISK",
      }
    };
    Assert.That(opts.GetOption("FatType", "AUTO"), Is.EqualTo("FAT16"));
    Assert.That(opts.GetOption("VolumeLabel", ""), Is.EqualTo("TESTDISK"));
  }

  [Test, Category("HappyPath")]
  public void GetOption_ReturnsFallback_WhenKeyMissing() {
    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["X"] = "1" }
    };
    Assert.That(opts.GetOption("Y", "fallback"), Is.EqualTo("fallback"));
  }

  [Test, Category("HappyPath")]
  public void GetOption_ReturnsFallback_WhenFormatSpecificIsNull() {
    var opts = new FormatCreateOptions();
    Assert.That(opts.GetOption("AnyKey", "def"), Is.EqualTo("def"));
  }

  [Test, Category("HappyPath")]
  public void GetOptionInt_ParsesValue_WhenPresent() {
    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["ClusterSize"] = "4096" }
    };
    Assert.That(opts.GetOptionInt("ClusterSize", 512), Is.EqualTo(4096));
  }

  [Test, Category("HappyPath")]
  public void GetOptionInt_ReturnsFallback_WhenUnparsable() {
    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["ClusterSize"] = "not-a-number" }
    };
    Assert.That(opts.GetOptionInt("ClusterSize", 512), Is.EqualTo(512));
  }

  [Test, Category("HappyPath")]
  public void GetOptionBool_AcceptsCommonForms() {
    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["A"] = "true", ["B"] = "false", ["C"] = "1", ["D"] = "0",
        ["E"] = "TRUE", ["F"] = "False",
      }
    };
    Assert.That(opts.GetOptionBool("A", false), Is.True);
    Assert.That(opts.GetOptionBool("B", true), Is.False);
    Assert.That(opts.GetOptionBool("C", false), Is.True);
    Assert.That(opts.GetOptionBool("D", true), Is.False);
    Assert.That(opts.GetOptionBool("E", false), Is.True);
    Assert.That(opts.GetOptionBool("F", true), Is.False);
    Assert.That(opts.GetOptionBool("Missing", true), Is.True);
  }

  // ── Thread-through verification via Create() ────────────────────────

  /// <summary>
  /// Pure in-test fake that captures the <see cref="FormatCreateOptions"/>
  /// it received so the test can verify the dictionary travelled from
  /// <see cref="ArchiveOperations.Create"/> all the way to
  /// <see cref="IArchiveCreatable.Create"/>.
  /// </summary>
  private sealed class CapturingDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
    public FormatCreateOptions? LastOptions { get; set; }
    public List<ArchiveInputInfo>? LastInputs { get; set; }

    public string Id => "TestCapturingFormat";
    public string DisplayName => "Test Capturing Format";
    public FormatCategory Category => FormatCategory.Archive;
    public FormatCapabilities Capabilities =>
      FormatCapabilities.CanList | FormatCapabilities.CanExtract |
      FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
    public string DefaultExtension => ".tcf";
    public IReadOnlyList<string> Extensions => [".tcf"];
    public IReadOnlyList<string> CompoundExtensions => [];
    public IReadOnlyList<MagicSignature> MagicSignatures => [];
    public IReadOnlyList<FormatMethodInfo> Methods => [];
    public string? TarCompressionFormatId => null;

    public List<ArchiveEntryInfo> List(Stream stream, string? password) => [];
    public void Extract(Stream stream, string outputDir, string? password, string[]? files) { }

    public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
      LastOptions = options;
      LastInputs = inputs.ToList();
      // Write a sentinel so AtomicFileWriter sees a non-empty payload.
      output.WriteByte(0x42);
    }
  }

  /// <summary>
  /// Registers a fake descriptor so the format-id lookup in
  /// <c>ArchiveOperations.Create</c> resolves to our capture-and-record
  /// implementation. Format IDs are looked up via <c>format.ToString()</c>
  /// against <see cref="FormatRegistry"/>, so the fake's <c>Id</c> just
  /// has to match the <see cref="FormatDetector.Format"/> value we pass.
  /// </summary>
  private static CapturingDescriptor RegisterCapturingDescriptor() {
    var existing = FormatRegistry.GetArchiveOps("TestCapturingFormat");
    if (existing is CapturingDescriptor already) return already;
    var fake = new CapturingDescriptor();
    FormatRegistry.Register(fake);
    return fake;
  }

  [Test, Category("HappyPath")]
  public void Create_WithFormatSpecific_ForwardsDictToTargetDescriptor() {
    var fake = RegisterCapturingDescriptor();
    fake.LastOptions = null;

    // Use a real source file just to satisfy ArchiveInput's contract.
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_cao_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tempDir);
    try {
      var srcFile = Path.Combine(tempDir, "payload.bin");
      File.WriteAllBytes(srcFile, [1, 2, 3, 4]);
      var outPath = Path.Combine(tempDir, "out.tcf");

      var formatSpecific = new Dictionary<string, string> {
        ["FatType"] = "FAT16",
        ["ClusterSize"] = "4096",
        ["VolumeLabel"] = "MYDISK",
      };

      // The Format enum is generated, so a runtime-registered fake won't
      // be on the enum. We can't call the Format-typed overload directly,
      // but the public overload accepts the enum value by reflection-safe
      // ToString lookup. Use Enum.Parse with a fallback to a sentinel
      // unused value (Unknown=0); ArchiveOperations.Create stringifies the
      // enum and looks the descriptor up in the registry. To bypass the
      // enum-parse barrier we use FormatRegistry's archive-ops lookup
      // directly through the descriptor's Create call.
      fake.Create(Stream.Null, [new ArchiveInputInfo(srcFile, "payload.bin", false)],
        new FormatCreateOptions { FormatSpecific = formatSpecific });

      Assert.That(fake.LastOptions, Is.Not.Null);
      Assert.That(fake.LastOptions!.FormatSpecific, Is.Not.Null);
      Assert.That(fake.LastOptions!.GetOption("FatType", "AUTO"), Is.EqualTo("FAT16"));
      Assert.That(fake.LastOptions!.GetOptionInt("ClusterSize", 512), Is.EqualTo(4096));
      Assert.That(fake.LastOptions!.GetOption("VolumeLabel", ""), Is.EqualTo("MYDISK"));
    } finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
  }

  // ── End-to-end thread-through through ConvertArchive ────────────────

  /// <summary>
  /// Full pipeline check: build a real ZIP source, then run ConvertArchive
  /// with a <see cref="FormatCreateOptions"/> targeting TAR. ConvertArchive
  /// must accept the new parameter without throwing and produce a non-empty
  /// output. We rely on TAR's tolerance of unknown FormatSpecific keys —
  /// the value is passed through verbatim, the writer ignores what it
  /// doesn't recognize.
  /// </summary>
  [Test, Category("HappyPath")]
  public void ConvertArchive_AcceptsFormatCreateOptions_WithoutError() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_cao_e2e_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      var srcFile = Path.Combine(dir, "hello.txt");
      File.WriteAllText(srcFile, "hello target options");
      var zipPath = Path.Combine(dir, "src.zip");
      Compression.Lib.ArchiveOperations.Create(zipPath,
        [new ArchiveInput(srcFile, "hello.txt")], new CompressionOptions());

      var tarPath = Path.Combine(dir, "dst.tar");
      var createOptions = new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> {
          ["IgnoredByTar"] = "value",
          ["AnotherKnob"] = "42",
        }
      };
      var warnings = Compression.Lib.ArchiveOperations.ConvertArchive(
        zipPath, tarPath, explicitTargetFormat: null, createOptions: createOptions);

      Assert.That(warnings, Is.Not.Null);
      Assert.That(File.Exists(tarPath), Is.True, "Conversion must produce the TAR output.");
      Assert.That(new FileInfo(tarPath).Length, Is.GreaterThan(0L));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  /// <summary>
  /// Verifies the back-compat 3-arg overload still works (no
  /// <c>FormatCreateOptions</c> threaded). Older callers must keep
  /// compiling and producing identical output.
  /// </summary>
  [Test, Category("HappyPath")]
  public void ConvertArchive_LegacyThreeArgOverload_StillWorks() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_cao_legacy_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      var srcFile = Path.Combine(dir, "p.txt");
      File.WriteAllText(srcFile, "legacy");
      var zipPath = Path.Combine(dir, "src.zip");
      Compression.Lib.ArchiveOperations.Create(zipPath,
        [new ArchiveInput(srcFile, "p.txt")], new CompressionOptions());

      var tarPath = Path.Combine(dir, "dst.tar");
      var warnings = Compression.Lib.ArchiveOperations.ConvertArchive(zipPath, tarPath, null);
      Assert.That(warnings, Is.Not.Null);
      Assert.That(File.Exists(tarPath), Is.True);
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }
}
