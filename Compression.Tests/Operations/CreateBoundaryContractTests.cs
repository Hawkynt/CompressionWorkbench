#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// The contract at the operations → registry create boundary, in two halves.
/// <para>
/// <b>Sentinel translation.</b> <see cref="MethodSpec"/> spells "no method preference" as the
/// literal name <c>"default"</c>; <see cref="FormatCreateOptions.MethodName"/> spells it as
/// <see langword="null"/>. Anything that lets the sentinel through hands every creator a method
/// literally named "default" — lenient creators ignore it, strict ones refuse it as unknown, and
/// the round trip breaks for the strict half only.
/// </para>
/// <para>
/// <b>Create-side resolution.</b> Reading resolves a shared extension by content; creating cannot,
/// because there is no content yet, so it resolves by capability instead. Every path that names a
/// file about to be written must ask <see cref="FormatDetector.DetectByExtensionForCreate"/>, never
/// the read-side lookup, whose first-claim-wins answer can be a descriptor that cannot create.
/// </para>
/// </summary>
[TestFixture]
public sealed class CreateBoundaryContractTests {

  // ── Sentinel translation ──────────────────────────────────────────

  /// <summary>
  /// Every spelling of "no preference" the parser can produce. <c>"default+"</c> and the bare
  /// <c>"+"</c> are the ones <see cref="MethodSpec.IsDefault"/> answers false for, because it also
  /// requires <c>!Optimize</c> — correct for the conversion-tier decision, wrong for this boundary.
  /// </summary>
  [TestCase(null)]
  [TestCase("")]
  [TestCase("   ")]
  [TestCase("default")]
  [TestCase("default+")]
  [TestCase("DEFAULT")]
  [TestCase("+")]
  [TestCase("++")]
  [Category("HappyPath")]
  public void NoPreferenceNeverCrossesTheBoundaryAsAMethodName(string? spelling) {
    var spec = MethodSpec.Parse(spelling);
    Assert.That(spec.EffectiveName, Is.Null,
      $"'{spelling}' parsed to name '{spec.Name}', which would reach creators as a real method name.");
  }

  [TestCase("deflate", "deflate")]
  [TestCase("store", "store")]
  [TestCase("lzma+", "lzma")]
  [TestCase("deflate++", "deflate")]
  [TestCase("PPMd", "ppmd")]
  [Category("HappyPath")]
  public void ARealMethodStillCrossesTheBoundary(string spelling, string expected) {
    Assert.That(MethodSpec.Parse(spelling).EffectiveName, Is.EqualTo(expected));
  }

  [Test, Category("HappyPath")]
  public void TheOptimizeFlagSurvivesTheSentinelTranslation()
    => Assert.That(MethodSpec.Parse("default+").Optimize, Is.True,
      "the '+' must still reach the creator as Optimize even though the name resolves to null");

  /// <summary>
  /// The behavioural half, on the strictest creator in the domain: EGG's
  /// <c>ResolveMethod</c> throws <see cref="NotSupportedException"/> for anything but store,
  /// deflate and auto. A leaked sentinel therefore fails the create outright rather than being
  /// silently ignored, which is what made this class of bug visible in the first place.
  /// </summary>
  [TestCase("default")]
  [TestCase("default+")]
  [TestCase("+")]
  [TestCase(null)]
  [Category("RoundTrip")]
  public void AStrictCreatorRoundTripsWithNoMethodPreference(string? spelling) {
    var dir = MakeTempDir();
    try {
      var src = Path.Combine(dir, "payload.txt");
      var content = "egg-sentinel-round-trip-" + (spelling ?? "<null>");
      File.WriteAllText(src, content);

      var archive = Path.Combine(dir, "out.egg");
      ArchiveOperations.Create(archive, [new ArchiveInput(src, "payload.txt")],
        new CompressionOptions { Method = MethodSpec.Parse(spelling) });

      var outDir = Path.Combine(dir, "extracted");
      Directory.CreateDirectory(outDir);
      ArchiveOperations.Extract(archive, outDir, password: null, files: null);

      Assert.That(File.ReadAllText(Path.Combine(outDir, "payload.txt")), Is.EqualTo(content));
    } finally { Directory.Delete(dir, true); }
  }

  // ── Create-side resolution ────────────────────────────────────────

  /// <summary>
  /// The extensions where the two resolvers genuinely disagree. If this stops being true the
  /// tests below stop testing anything, so assert it rather than assume it.
  /// </summary>
  [TestCase(".bundle")]
  [TestCase(".vib")]
  [Category("HappyPath")]
  public void TheTwoResolversDisagreeOnTheseExtensions(string ext) {
    var readSide = FormatDetector.DetectByExtension("x" + ext);
    var createSide = FormatDetector.DetectByExtensionForCreate("x" + ext);
    Assert.That(createSide, Is.Not.EqualTo(readSide),
      $"{ext}: the read-side lookup already picks a creatable claimant, so nothing here is at risk");

    FormatRegistration.EnsureInitialized();
    Assert.That(FormatRegistry.GetArchiveOps(readSide.ToString()), Is.Not.InstanceOf<IArchiveCreatable>(),
      $"{ext}: the read-side claimant {readSide} can create after all");
    Assert.That(FormatRegistry.GetArchiveOps(createSide.ToString()), Is.InstanceOf<IArchiveCreatable>(),
      $"{ext}: the create-side claimant {createSide} cannot create");
  }

  /// <summary>
  /// <see cref="ArchiveWriter.Create(string, FormatCreateOptions?)"/> names a file that does not
  /// exist yet. Resolving it read-side handed <c>.bundle</c> to Mach-O and <c>.vib</c> to Veeam,
  /// and the very next line then rejected them for not supporting creation.
  /// </summary>
  [TestCase(".bundle")]
  [TestCase(".vib")]
  [Category("HappyPath")]
  public void ArchiveWriterResolvesASharedExtensionByCapability(string ext) {
    var dir = MakeTempDir();
    try {
      var path = Path.Combine(dir, "target" + ext);
      Assert.DoesNotThrow(() => {
        using var writer = ArchiveWriter.Create(path);
      }, $"ArchiveWriter refused to open a writer for {ext}");
    } finally { Directory.Delete(dir, true); }
  }

  /// <summary>
  /// <see cref="ArchiveOperations.Convert"/> resolves its source by content and its target by
  /// capability. Only the target moved; a conversion into an unshared extension must land on the
  /// same format it always did.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void ConvertResolvesItsTargetByCapability() {
    var dir = MakeTempDir();
    try {
      var src = Path.Combine(dir, "payload.txt");
      File.WriteAllText(src, "convert-target-resolution");
      var zip = Path.Combine(dir, "in.zip");
      ArchiveOperations.Create(zip, [new ArchiveInput(src, "payload.txt")], new CompressionOptions());

      var target = Path.Combine(dir, "out.bundle");
      Assert.DoesNotThrow(() => ArchiveOperations.Convert(zip, target, password: null),
        "converting into a shared extension must pick the claimant that can create");
      Assert.That(File.Exists(target), Is.True);
    } finally { Directory.Delete(dir, true); }
  }

  // ── Option forwarding ─────────────────────────────────────────────

  /// <summary>
  /// Stream formats accepted <c>--level</c> and then dropped it: the create path called the
  /// no-options <c>Compress</c> overload even for the nine descriptors that override the
  /// options-aware one. Store-level and maximum-level gzip of compressible input must differ.
  /// </summary>
  [Test, Category("HappyPath")]
  public void StreamCreationHonoursTheRequestedLevel() {
    var dir = MakeTempDir();
    try {
      var src = Path.Combine(dir, "payload.txt");
      File.WriteAllText(src, string.Concat(Enumerable.Repeat("compressible-payload-", 4096)));

      var low = Path.Combine(dir, "low.gz");
      var high = Path.Combine(dir, "high.gz");
      ArchiveOperations.Create(low, [new ArchiveInput(src, "payload.txt")],
        new CompressionOptions { Level = 0 });
      ArchiveOperations.Create(high, [new ArchiveInput(src, "payload.txt")],
        new CompressionOptions { Level = 9 });

      Assert.That(new FileInfo(low).Length, Is.Not.EqualTo(new FileInfo(high).Length),
        "level 0 and level 9 produced byte-identical gzip output, so the level never reached the descriptor");

      foreach (var path in (string[])[low, high])
        Assert.That(ArchiveOperations.DecompressFile(path, FormatDetector.Format.Gzip),
          Is.EqualTo(File.ReadAllBytes(src)), $"{Path.GetFileName(path)} did not round-trip");
    } finally { Directory.Delete(dir, true); }
  }

  /// <summary>
  /// <see cref="FormatCreateOptions.Copy"/> is what keeps the conversion pipeline from dropping
  /// the caller's options; a field it forgets is a knob that silently stops working.
  /// </summary>
  [Test, Category("HappyPath")]
  public void CopyCarriesEveryCreateOption() {
    var original = new FormatCreateOptions {
      MethodName = "deflate", Password = "secret", Optimize = true, Level = 7, DictSize = 1 << 20,
      WordSize = 64, Threads = 4, SolidSize = 1 << 24, ForceCompress = true, EncryptFilenames = true,
      EncryptionMethod = "zipcrypto", IncompressiblePaths = ["/tmp/already.jpg"],
      FormatSpecific = { ["ClusterSize"] = "4096" },
    };

    var copy = original.Copy();

    Assert.Multiple(() => {
      Assert.That(copy.MethodName, Is.EqualTo("deflate"));
      Assert.That(copy.Method, Is.EqualTo("deflate"));
      Assert.That(copy.Password, Is.EqualTo("secret"));
      Assert.That(copy.Optimize, Is.True);
      Assert.That(copy.Level, Is.EqualTo(7));
      Assert.That(copy.DictSize, Is.EqualTo(1 << 20));
      Assert.That(copy.WordSize, Is.EqualTo(64));
      Assert.That(copy.Threads, Is.EqualTo(4));
      Assert.That(copy.SolidSize, Is.EqualTo(1 << 24));
      Assert.That(copy.ForceCompress, Is.True);
      Assert.That(copy.EncryptFilenames, Is.True);
      Assert.That(copy.EncryptionMethod, Is.EqualTo("zipcrypto"));
      Assert.That(copy.IncompressiblePaths, Is.EquivalentTo(new[] { "/tmp/already.jpg" }));
      Assert.That(copy.GetOption("clustersize", "?"), Is.EqualTo("4096"),
        "the copied bag must keep the case-insensitive comparer");
    });
    Assert.That(copy.FormatSpecific, Is.Not.SameAs(original.FormatSpecific));
  }

  private static string MakeTempDir() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_boundary_" + Guid.NewGuid().ToString("N")[..10]);
    Directory.CreateDirectory(dir);
    return dir;
  }
}
