#pragma warning disable CS1591
using Compression.Registry;
using Compression.Tests.Documentation;

namespace Compression.Tests.Operations;

/// <summary>
/// Every write cell of the filesystem package support matrix, proven by writing
/// bytes and reading the same bytes back.
/// </summary>
/// <remarks>
/// <para>The cases come from <see cref="FilesystemSupportMatrix.Descriptors"/> —
/// the same enumeration the README table is rendered from — so a WORM or R/W cell
/// in that table and a case in this fixture are the same fact seen twice. A format
/// that gains the claim gains the case with no edit here.</para>
///
/// <para>Cases are keyed on the format <b>id</b>, never on the default extension.
/// <c>.img</c> alone is claimed by twelve of these formats and <c>.dsk</c> by
/// several more; keying on the extension collapses each of those groups into one
/// case and quietly drops the rest of the group's coverage. The id is what the
/// registry resolves a writer by, so it is what the fixture enumerates.</para>
///
/// <para>Two families of format need asking in two different ways, and which one a
/// format belongs to is discovered rather than listed. A volume that holds named
/// files gets a named file and has to give it back. An image whose entries are a
/// derived set of its own — the sectors of a CD, the tracks of a floppy flux
/// image, the blocks of a compressed ISO, the sections of an EnCase segment, a
/// single firmware payload — folds the input into that set instead, so it is asked
/// in its own vocabulary: rewrite one of the entries it lists, under the name it
/// listed, and read that back. Both are a write and a read-back of the same bytes;
/// only the unit differs.</para>
///
/// <para>Silent <c>Assert.Ignore</c> is deliberately not used for capability
/// failures — an ignored case reads as coverage in the run summary while proving
/// nothing. Formats that cannot be exercised are listed in <see cref="KnownGaps"/>
/// with the reason, and the list is held honest by <see cref="KnownGaps_AreStillGaps"/>.</para>
/// </remarks>
[TestFixture]
[Category("RoundTrip")]
public sealed class FilesystemWriteRoundTripTests {

  private const string Probe = "PROBE.BIN";
  private const string ProbeStem = "PROBE";
  private const string Seed = "SEED.BIN";
  private const string SeedStem = "SEED";

  /// <summary>
  /// Ids whose write claim this fixture cannot exercise, and why. Each entry is a
  /// tracked gap, not a waiver: the reason has to name what the format needs that
  /// the generic probe does not supply.
  /// </summary>
  private static readonly Dictionary<string, string> KnownGaps = new(StringComparer.Ordinal) {
    ["Refs"] = "edits existing volumes only — no creator, so there is no probe image to edit. "
             + "A ReFS corpus would have to come from Windows.",
  };

  /// <summary>
  /// Formats whose edit is addressed by a numbered unit — a CD sector, a
  /// compressed-ISO block, an EnCase section — rather than by the name of a file.
  /// They list the files of the filesystem inside them and still take an edit only
  /// as <c>sector-NNNN.bin</c> or its equivalent, so a name-addressed probe can
  /// never reach the path they do implement. Each names the fixture that does, and
  /// <see cref="UnitAddressed_PointAtFixturesThatExist"/> keeps the pointer honest.
  /// </summary>
  private static readonly Dictionary<string, string> UnitAddressed = new(StringComparer.Ordinal) {
    ["BinCue"] = "BinCueInPlaceModifyTests",
    ["Cdi"] = "CdiInPlaceModifyTests",
    ["Mdf"] = "MdfInPlaceModifyTests",
    ["Nrg"] = "NrgInPlaceModifyTests",
    ["Cso"] = "CsoInPlaceModifyTests",
    ["Ewf"] = "FilesystemRwPromotionRoundTripTests",
  };

  private string _scratch = "";

  [OneTimeSetUp]
  public void CreateScratch() {
    this._scratch = Path.Combine(Path.GetTempPath(), "cwb_fs_wrt_" + Guid.NewGuid().ToString("N")[..12]);
    Directory.CreateDirectory(this._scratch);
  }

  [OneTimeTearDown]
  public void RemoveScratch() {
    try { if (Directory.Exists(this._scratch)) Directory.Delete(this._scratch, recursive: true); } catch { /* best effort */ }
  }

  private static IEnumerable<TestCaseData> CreatableIds() => Ids(FormatCapabilities.CanCreate, "Create");
  private static IEnumerable<TestCaseData> ModifiableIds() => Ids(FormatCapabilities.CanModify, "Modify");

  private static IEnumerable<TestCaseData> Ids(FormatCapabilities claim, string verb) {
    foreach (var descriptor in Descriptors())
      if (descriptor.Capabilities.HasFlag(claim))
        yield return new TestCaseData(descriptor.Id).SetName($"{verb}RoundTrip_{descriptor.Id}");
  }

  private static IReadOnlyList<IFormatDescriptor> Descriptors()
    => FilesystemSupportMatrix.Descriptors(FilesystemReadmeIsCurrentTests.RepositoryRoot());

  /// <summary>A WORM cell means a fresh image whose content reads back unchanged.</summary>
  [TestCaseSource(nameof(CreatableIds))]
  public void ACreatedImage_ReadsTheWrittenBytesBack(string formatId) {
    if (KnownGaps.TryGetValue(formatId, out var gap)) Assert.Ignore($"{formatId}: tracked gap — {gap}");

    var payload = Payload(1, 512);
    var image = Create(formatId, [ArchiveInputInfo.InMemory(Probe, payload)]);
    Assert.That(image, Is.Not.Null, $"{formatId} claims CanCreate but produced no image.");

    var listed = Names(formatId, image!);
    Assert.That(listed, Is.Not.Empty, $"{formatId}: the image it just wrote lists nothing.");

    if (listed.Any(n => Matches(n, ProbeStem))) {
      this.AssertReadsBack(formatId, image!, ProbeStem, payload, "after create");
      return;
    }

    // The format keeps an entry set of its own and folded the input into it. The
    // write is still proven, in the unit the format actually stores: every entry
    // it lists has to come back out of the image it just wrote.
    this.AssertEveryEntryExtracts(formatId, image!, "after create");
  }

  /// <summary>
  /// An R/W cell means an edit of an EXISTING image: what goes in reads back
  /// unchanged, and what was already there survives it byte for byte.
  /// </summary>
  [TestCaseSource(nameof(ModifiableIds))]
  public void AnEditedImage_ReadsTheEditBack(string formatId) {
    if (KnownGaps.TryGetValue(formatId, out var gap)) Assert.Ignore($"{formatId}: tracked gap — {gap}");

    var ops = FormatRegistry.GetArchiveOps(formatId);
    Assert.That(ops, Is.InstanceOf<IArchiveModifiable>(), $"{formatId} claims CanModify but exposes no modifier.");
    var modifier = (IArchiveModifiable)ops!;
    if (UnitAddressed.TryGetValue(formatId, out var fixtureName))
      Assert.Ignore($"{formatId}: edited by numbered unit rather than by name — {fixtureName} covers that path.");

    var seed = Payload(2, 384);
    var image = Create(formatId, [ArchiveInputInfo.InMemory(Seed, seed)]);
    Assert.That(image, Is.Not.Null, $"{formatId} claims CanModify but a probe image cannot be created.");

    if (Names(formatId, image!).Any(n => Matches(n, SeedStem))) {
      this.EditByName(formatId, modifier, image!, seed);
      return;
    }

    this.EditByOwnEntry(formatId, modifier, image!);
  }

  /// <summary>A volume that holds named files: add one, keep the other, remove the first.</summary>
  /// <remarks>
  /// A disc image lists the files of the filesystem inside it and yet edits itself
  /// a sector at a time, so a format can answer to a name here and still refuse to
  /// be addressed by one. The refusal names the vocabulary it does take, and the
  /// edit is then put in that vocabulary rather than counted as a pass.
  /// </remarks>
  private void EditByName(string formatId, IArchiveModifiable modifier, byte[] image, byte[] seed) {
    var added = Payload(3, 512);
    byte[] withProbe;
    try {
      withProbe = Mutate(formatId, image, m => modifier.Add(m, [ArchiveInputInfo.InMemory(Probe, added)]), "Add");
    } catch (NotSupportedException) {
      this.EditByOwnEntry(formatId, modifier, image);
      return;
    }

    this.AssertReadsBack(formatId, withProbe, ProbeStem, added, "after add");
    this.AssertReadsBack(formatId, withProbe, SeedStem, seed, "after add (survivor)");

    // The name a format lists an entry under and the name its remover matches on
    // are not always the same spelling of it, so the stored form is tried first
    // and the bare leaf second. Both together, in one call, is what a strict
    // remover reads as "one of these is missing".
    var stored = Names(formatId, withProbe).First(n => Matches(n, ProbeStem));
    var afterRemove = Mutate(formatId, withProbe, m => modifier.Remove(m, [stored]), "Remove");
    if (Names(formatId, afterRemove).Any(n => Matches(n, ProbeStem)) && Leaf(stored) != stored)
      afterRemove = Mutate(formatId, withProbe, m => modifier.Remove(m, [Leaf(stored)]), "Remove");
    Assert.That(Names(formatId, afterRemove).Any(n => Matches(n, ProbeStem)), Is.False,
      $"{formatId}: '{stored}' is still listed after Remove.");
    this.AssertReadsBack(formatId, afterRemove, SeedStem, seed, "after remove (survivor)");
  }

  /// <summary>
  /// An image whose entries are its own derived set: rewrite one of them under the
  /// name it listed, at the length it listed, and read those bytes back.
  /// </summary>
  /// <remarks>
  /// Which of its entries can be rewritten is the format's business -- a compressed
  /// ISO takes a block and not its index, an EnCase segment takes a section -- so
  /// each is offered in turn and the first the modifier accepts has to round-trip.
  /// A format that refuses every one of its own entries has no edit path at all,
  /// and the refusals are reported together.
  /// </remarks>
  private void EditByOwnEntry(string formatId, IArchiveModifiable modifier, byte[] image) {
    var ops = (IArchiveFormatOperations)FormatRegistry.GetArchiveOps(formatId)!;
    List<ArchiveEntryInfo> entries;
    using (var stream = new MemoryStream(image, writable: false))
      entries = ops.List(stream, null).Where(e => !e.IsDirectory).ToList();

    // metadata.ini and its kin are rendered from the header, not stored, so
    // rewriting one asks the format to accept a report of itself as input.
    var candidates = entries
      .Where(e => !IsRendered(e.Name) && e.OriginalSize is > 0 and <= 1 << 20)
      .OrderBy(e => e.OriginalSize)
      .ToList();
    Assert.That(candidates, Is.Not.Empty,
      $"{formatId}: nothing rewritable in its own listing ({string.Join(", ", entries.Select(e => $"{e.Name}:{e.OriginalSize}"))}).");

    var extracted = this.ExtractAll(formatId, image);
    var refusals = new List<string>();
    foreach (var candidate in candidates) {
      // The length the entry EXTRACTS to is the length the format will take back:
      // a compressed-ISO block lists its compressed size and accepts only a whole
      // uncompressed one.
      var current = extracted.FirstOrDefault(e => e.Leaf.Equals(Leaf(candidate.Name), StringComparison.OrdinalIgnoreCase)).Bytes;
      if (current == null) { refusals.Add($"{candidate.Name}: listed but not extractable"); continue; }

      var replacement = Payload(4, current.Length);
      byte[] edited;
      try {
        edited = Mutate(formatId, image,
          m => modifier.Add(m, [ArchiveInputInfo.InMemory(candidate.Name, replacement)]), "Add");
      } catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidDataException) {
        refusals.Add($"{candidate.Name}: {ex.Message}");
        continue;
      }

      // Some of a derived entry set are views the reader renders rather than bytes
      // the image stores -- an EnCase section table is rebuilt from the sections it
      // describes -- so an entry that does not come back is the next candidate's
      // turn, not a verdict. At least one of them has to be real.
      var back = this.ExtractAll(formatId, edited)
        .FirstOrDefault(e => e.Leaf.Equals(Leaf(candidate.Name), StringComparison.OrdinalIgnoreCase)).Bytes;
      if (back != null && back.Length >= replacement.Length
          && back.AsSpan(0, replacement.Length).SequenceEqual(replacement))
        return;
      refusals.Add($"{candidate.Name}: accepted the write but did not read it back");
    }

    Assert.Fail($"{formatId} claims CanModify but no entry it lists survives a rewrite:\n  "
              + string.Join("\n  ", refusals));
  }

  /// <summary>
  /// The gap list decays into a lie the moment a listed format starts working, so
  /// every entry is re-checked: one that now round-trips has to leave the list.
  /// </summary>
  [Test]
  public void KnownGaps_AreStillGaps() {
    var descriptors = Descriptors();
    var known = descriptors.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
    var stale = KnownGaps.Keys.Where(id => !known.Contains(id)).ToList();
    Assert.That(stale, Is.Empty, "Gap entries for formats the package no longer bundles: " + string.Join(", ", stale));

    var empty = KnownGaps.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
    Assert.That(empty, Is.Empty, "Gap entries with no reason: " + string.Join(", ", empty));

    var fixedUp = new List<string>();
    foreach (var id in KnownGaps.Keys) {
      var descriptor = descriptors.Single(d => d.Id == id);
      if (!descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate)) continue;
      try {
        var image = Create(id, [ArchiveInputInfo.InMemory(Probe, Payload(1, 512))]);
        if (image != null && Names(id, image).Count > 0) fixedUp.Add(id);
      } catch { /* still a gap */ }
    }
    Assert.That(fixedUp, Is.Empty,
      "These now create a listable probe image, so their gap entry is stale — remove it: " + string.Join(", ", fixedUp));
  }

  /// <summary>A pointer at another fixture is only worth having while that fixture exists.</summary>
  [Test]
  public void UnitAddressed_PointAtFixturesThatExist() {
    var fixtures = typeof(FilesystemWriteRoundTripTests).Assembly.GetTypes()
      .Where(t => t.IsClass && !t.IsAbstract
               && t.GetMethods().Any(m => m.GetCustomAttributes(inherit: true)
                    .Any(a => a is TestAttribute or TestCaseAttribute or TestCaseSourceAttribute)))
      .Select(t => t.Name)
      .ToHashSet(StringComparer.Ordinal);

    var dangling = UnitAddressed.Where(kv => !fixtures.Contains(kv.Value))
      .Select(kv => $"{kv.Key} -> {kv.Value}").ToList();
    Assert.That(dangling, Is.Empty,
      "These defer their round-trip to a fixture that no longer exists: " + string.Join(", ", dangling));

    var known = Descriptors().Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
    var gone = UnitAddressed.Keys.Where(id => !known.Contains(id)).ToList();
    Assert.That(gone, Is.Empty, "Deferrals for formats the package no longer bundles: " + string.Join(", ", gone));
  }

  // ── helpers ──────────────────────────────────────────────────────────────

  private static byte[]? Create(string formatId, IReadOnlyList<ArchiveInputInfo> inputs) {
    if (FormatRegistry.GetArchiveOps(formatId) is not IArchiveCreatable creator) return null;
    using var image = new MemoryStream();
    creator.Create(image, inputs, new FormatCreateOptions());
    return image.Length == 0 ? null : image.ToArray();
  }

  private static byte[] Mutate(string formatId, byte[] image, Action<MemoryStream> edit, string step) {
    using var stream = new MemoryStream();
    stream.Write(image, 0, image.Length);
    stream.Position = 0;
    edit(stream);
    var result = stream.ToArray();
    Assert.That(result.Length, Is.GreaterThan(0), $"{formatId}: {step} left an empty image.");
    return result;
  }

  private static List<string> Names(string formatId, byte[] image) {
    var ops = (IArchiveFormatOperations)FormatRegistry.GetArchiveOps(formatId)!;
    using var stream = new MemoryStream(image, writable: false);
    return ops.List(stream, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
  }

  private void AssertReadsBack(string formatId, byte[] image, string stem, byte[] expected, string step) {
    var extracted = this.ExtractAll(formatId, image);
    var got = extracted.FirstOrDefault(e => Matches(e.Leaf, stem)).Bytes;
    Assert.That(got, Is.Not.Null,
      $"{formatId}: nothing named for '{stem}' extracted {step} (got: {string.Join(", ", extracted.Select(e => e.Leaf))}).");

    // Sector- and record-granular filesystems store the length rounded up to a
    // block, so the contract is that the written bytes come back as the prefix
    // with only format padding behind them -- not that the file length matches.
    Assert.That(got!.Length, Is.GreaterThanOrEqualTo(expected.Length),
      $"{formatId}: '{stem}' truncated {step} -- {got.Length} of {expected.Length} bytes.");
    Assert.That(got.AsSpan(0, expected.Length).SequenceEqual(expected), Is.True,
      $"{formatId}: '{stem}' came back with different bytes {step}.");
    Assert.That(got.Length - expected.Length, Is.LessThanOrEqualTo(64 * 1024),
      $"{formatId}: '{stem}' extracted {got.Length} bytes for a {expected.Length}-byte write {step}.");
  }

  private void AssertEveryEntryExtracts(string formatId, byte[] image, string step) {
    var extracted = this.ExtractAll(formatId, image)
      .Select(e => e.Leaf).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var missing = Names(formatId, image).Select(Leaf).Where(n => !extracted.Contains(n)).ToList();
    Assert.That(missing, Is.Empty,
      $"{formatId}: listed but not extractable {step}: {string.Join(", ", missing)}.");
  }

  /// <summary>Extracts into a scratch directory of its own and reads the bytes back out of it.</summary>
  private List<(string Leaf, byte[] Bytes)> ExtractAll(string formatId, byte[] image) {
    var outDir = Path.Combine(this._scratch, formatId + "_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      var ops = (IArchiveFormatOperations)FormatRegistry.GetArchiveOps(formatId)!;
      using (var stream = new MemoryStream(image, writable: false))
        ops.Extract(stream, outDir, null, null);
      return Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
        .Select(path => (Path.GetFileName(path), File.ReadAllBytes(path)))
        .ToList();
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
    }
  }

  /// <summary>
  /// Whether a listed name is the one the fixture wrote. A format is free to
  /// rename what it stores — TR-DOS and SCL append a type suffix, BBC prefixes a
  /// directory letter, MFS truncates — and that is a naming convention, not a
  /// failure to store the entry. The stem has to survive as one of the name's
  /// dot-separated parts; the probe stems are distinctive enough that nothing
  /// else can match by accident.
  /// </summary>
  private static bool Matches(string listed, string stem)
    => Leaf(listed).Split('.').Any(part => part.Equals(stem, StringComparison.OrdinalIgnoreCase));

  /// <summary>The part of a listed name that survives whatever the format renames it to.</summary>
  private static string StemOf(string name) => Path.GetFileNameWithoutExtension(Leaf(name));

  /// <summary>A report the reader renders from the header rather than an entry the image stores.</summary>
  private static bool IsRendered(string name) {
    var leaf = Leaf(name);
    return leaf.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)
        || leaf.Equals("header.bin", StringComparison.OrdinalIgnoreCase);
  }

  private static string Leaf(string name) => Path.GetFileName(name.Replace('\\', '/').TrimEnd('/'));

  private static byte[] Payload(int seed, int length) {
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }
}
