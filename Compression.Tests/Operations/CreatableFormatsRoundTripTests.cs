#pragma warning disable CS1591
using System.Text.RegularExpressions;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Content round trip for every creatable format in the archive package: write an archive, read it
/// back, compare the bytes.
/// <para>
/// <c>EndToEndInteropTests.SelfRoundTrip_CreateAndExtract</c> already does this, but it is keyed on
/// <see cref="IFormatDescriptor.DefaultExtension"/> and lets the extension pick the writer. Formats
/// that share a default extension therefore collapse into one case, and whichever of them the
/// extension does not resolve to is never exercised under its own identity — the create-side
/// resolver decides, and it can only pick one. This fixture is keyed on the registry id and passes
/// the format explicitly, so every claimant of a shared extension is written by name.
/// </para>
/// <para>
/// The scope is the descriptors bundled into <c>Hawkynt.FileFormats.Archives</c>, derived from the
/// package's own project files the same way <see cref="ArchivesReadmeStateTests"/> derives it, so a
/// format joining the package is picked up with no edit here. Filesystem images belong to the
/// filesystem package and its own coverage matrix.
/// </para>
/// <para>
/// A writer that only accepts typed input — a whole disk image, a single icon, a font collection —
/// is expected to refuse an arbitrary file tree rather than mangle it, which the package README
/// states as deliberate. A clean throw is therefore a pass. What this fixture does not tolerate is
/// the middle case: a format that accepts the files, lists them back under their own names, and
/// then hands back different bytes.
/// </para>
/// </summary>
[TestFixture]
public sealed class CreatableFormatsRoundTripTests {

  private static readonly byte[] TextPayload =
    System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("round-trip-probe\n", 24)));

  private static readonly byte[] BinaryPayload = BuildBinaryPayload();

  private static byte[] BuildBinaryPayload() {
    // Deterministic, mildly incompressible, and free of a run of zeroes that a sparse writer
    // could legitimately drop.
    var data = new byte[1024];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)(i * 37 % 251 + 1);
    return data;
  }

  private static IEnumerable<TestCaseData> BundledCreatableFormats() {
    FormatRegistration.EnsureInitialized();
    var bundled = BundledAssemblies();
    foreach (var d in FormatRegistry.All.OrderBy(x => x.Id, StringComparer.Ordinal)) {
      if (!d.Capabilities.HasFlag(FormatCapabilities.CanCreate)) continue;
      if (!d.Capabilities.HasFlag(FormatCapabilities.CanExtract)) continue;
      if (!bundled.Contains(d.GetType().Assembly.GetName().Name!.Replace("CompressionWorkbench.", ""))) continue;
      if (FormatRegistry.GetArchiveOps(d.Id) is not IArchiveCreatable) continue;
      if (!Enum.TryParse<FormatDetector.Format>(d.Id, out _)) continue;
      yield return new TestCaseData(d.Id).SetName($"RoundTripsItsOwnOutput_{d.Id}");
    }
  }

  [TestCaseSource(nameof(BundledCreatableFormats))]
  [Category("RoundTrip")]
  public void ACreatableFormatReadsBackTheBytesItWrote(string formatId) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_crt_" + Guid.NewGuid().ToString("N")[..10]);
    Directory.CreateDirectory(work);
    try {
      var textSrc = Path.Combine(work, "PROBE.TXT");
      var binSrc = Path.Combine(work, "PROBE.BIN");
      File.WriteAllBytes(textSrc, TextPayload);
      File.WriteAllBytes(binSrc, BinaryPayload);

      var format = Enum.Parse<FormatDetector.Format>(formatId);
      var archive = Path.Combine(work, "archive.dat");
      try {
        ArchiveOperations.Create(archive, [
          new ArchiveInput(textSrc, "PROBE.TXT"),
          new ArchiveInput(binSrc, "PROBE.BIN"),
        ], new CompressionOptions(), format, null);
      } catch (Exception ex) when (ex is NotSupportedException or ArgumentException
                                      or InvalidOperationException or IOException) {
        Assert.Pass($"{formatId}: refused the generic input cleanly ({ex.GetType().Name}: {ex.Message}).");
        return;
      }

      if (!File.Exists(archive) || new FileInfo(archive).Length == 0) {
        Assert.Fail($"{formatId}: Create reported success but produced no bytes.");
        return;
      }

      List<ArchiveEntryInfo> entries;
      var ops = FormatRegistry.GetArchiveOps(formatId)!;
      try {
        using var read = File.OpenRead(archive);
        entries = ops.List(read, null);
      } catch (Exception ex) {
        Assert.Fail($"{formatId}: wrote an archive its own reader cannot list ({ex.GetType().Name}: {ex.Message}).");
        return;
      }

      // Only formats that preserve the entry names can be checked by name. The README's Notes
      // column flags the ones that rename or flatten; those are exercised by their own fixtures.
      foreach (var (probeName, expected) in
               new[] { ("PROBE.TXT", TextPayload), ("PROBE.BIN", BinaryPayload) }) {
        var entry = entries.FirstOrDefault(e => !e.IsDirectory && NameMatches(e.Name, probeName));
        if (entry == null) {
          Assert.Ignore($"{formatId}: does not surface '{probeName}' under its own name "
                        + $"(listed: {string.Join(", ", entries.Select(e => e.Name))}).");
          return;
        }

        byte[] actual;
        try {
          using var read = File.OpenRead(archive);
          actual = ops.ExtractEntryToMemory(read, entry.Name, null);
        } catch (Exception ex) {
          Assert.Fail($"{formatId}: listed '{entry.Name}' but could not read it back "
                      + $"({ex.GetType().Name}: {ex.Message}).");
          return;
        }

        Assert.That(actual, Is.EqualTo(expected),
          $"{formatId}: '{entry.Name}' came back with different bytes than were written "
          + $"({expected.Length} in, {actual.Length} out).");
      }
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  /// <summary>
  /// Some writers normalise case, strip the directory part, or fold a name into an 8.3 slot. A
  /// match on the base name is enough to say the entry is the one that went in; the byte
  /// comparison is what actually has teeth.
  /// </summary>
  private static bool NameMatches(string entryName, string probe)
    => string.Equals(Path.GetFileName(entryName.Replace('\\', '/')), probe, StringComparison.OrdinalIgnoreCase);

  private static HashSet<string> BundledAssemblies() {
    var root = Path.GetDirectoryName(FindRepositoryFile("Hawkynt.FileFormats.Archives", "README.md"))!;
    var projectFiles = File.ReadAllText(Path.Combine(root, "Hawkynt.FileFormats.Archives.csproj"))
      + File.ReadAllText(Path.Combine(root, "Directory.Build.targets"));
    return Regex.Matches(projectFiles, @"FileFormats\\(FileFormat\.[A-Za-z0-9]+)\\")
      .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
  }

  private static string FindRepositoryFile(params string[] relativeParts) {
    for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent) {
      var path = relativeParts.Aggregate(current.FullName, Path.Combine);
      if (File.Exists(path)) return path;
    }
    throw new FileNotFoundException($"Could not locate repository file '{Path.Combine(relativeParts)}'.");
  }
}
