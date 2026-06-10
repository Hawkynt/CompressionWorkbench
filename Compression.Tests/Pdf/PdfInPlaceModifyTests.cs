#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Pdf;

namespace Compression.Tests.Pdf;

/// <summary>
/// Locks the contract that PDF Add/Remove are true in-place R/W per
/// ISO&#160;32000-1 §7.5.6 incremental updates: every byte before the
/// original <c>%%EOF</c> stays byte-identical, Remove only adds an xref
/// tombstone (no data overwrite), and the reader honours the trailer chain.
/// </summary>
[TestFixture]
public class PdfInPlaceModifyTests {

  // ── Helpers ─────────────────────────────────────────────────────────────

  private static byte[] BuildSeedPdf(string? attachmentName = null, byte[]? attachmentBytes = null) {
    var w = new PdfWriter();
    if (attachmentName != null && attachmentBytes != null)
      w.AddFile(attachmentName, attachmentBytes);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  private static MemoryStream IntoGrowableStream(byte[] bytes) {
    var ms = new MemoryStream(bytes.Length * 4);
    ms.Write(bytes);
    ms.SetLength(bytes.Length);
    ms.Position = 0;
    return ms;
  }

  // ── AddFile ─────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddFile_NewAttachment_ReadsBack() {
    var seed = BuildSeedPdf("seed.txt", "SEEDDATA"u8.ToArray());
    using var ms = IntoGrowableStream(seed);
    PdfInPlaceModifier.AddFile(ms, "fresh.bin", "FRESHDATA"u8.ToArray());

    ms.Position = 0;
    var r = new PdfReader(ms);
    var byName = r.Entries.Where(e => e.Filter == "EmbeddedFile").ToDictionary(e => e.Name);
    Assert.That(byName.ContainsKey("fresh.bin"), Is.True);
    Assert.That(Encoding.ASCII.GetString(r.Extract(byName["fresh.bin"])), Is.EqualTo("FRESHDATA"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_OriginalBytesPreservedByteIdentical() {
    var seed = BuildSeedPdf("seed.txt", "SEEDDATA"u8.ToArray());
    using var ms = IntoGrowableStream(seed);
    PdfInPlaceModifier.AddFile(ms, "added.bin", "ADDEDDATA"u8.ToArray());

    // The first `seed.Length` bytes of the modified stream MUST be exactly
    // the original seed bytes — this is the byte-identical preservation
    // guarantee of an ISO 32000-1 incremental update.
    var after = ms.ToArray();
    Assert.That(after.Length, Is.GreaterThan(seed.Length),
      "the file should have grown (incremental section was appended).");
    var prefix = after.AsSpan(0, seed.Length).ToArray();
    Assert.That(prefix, Is.EqualTo(seed),
      "every byte before the original %%EOF must stay byte-identical.");
  }

  [Test, Category("RoundTrip")]
  public void AddFile_BothOldAndNewAttachmentReadable() {
    var seed = BuildSeedPdf("first.txt", "FIRSTDATA"u8.ToArray());
    using var ms = IntoGrowableStream(seed);
    PdfInPlaceModifier.AddFile(ms, "second.txt", "SECONDDATA"u8.ToArray());

    ms.Position = 0;
    var r = new PdfReader(ms);
    var byName = r.Entries.Where(e => e.Filter == "EmbeddedFile").ToDictionary(e => e.Name);
    Assert.That(Encoding.ASCII.GetString(r.Extract(byName["first.txt"])), Is.EqualTo("FIRSTDATA"));
    Assert.That(Encoding.ASCII.GetString(r.Extract(byName["second.txt"])), Is.EqualTo("SECONDDATA"));
  }

  [Test, Category("RoundTrip")]
  public void AddFiles_BulkAdd_AllReadable() {
    var seed = BuildSeedPdf("orig.txt", "ORIG"u8.ToArray());
    using var ms = IntoGrowableStream(seed);
    PdfInPlaceModifier.AddFiles(ms, new (string, byte[])[] {
      ("a.bin", "ALPHA"u8.ToArray()),
      ("b.bin", "BETA"u8.ToArray()),
      ("c.bin", "GAMMA"u8.ToArray()),
    });

    ms.Position = 0;
    var r = new PdfReader(ms);
    var byName = r.Entries.Where(e => e.Filter == "EmbeddedFile").ToDictionary(e => e.Name);
    Assert.That(Encoding.ASCII.GetString(r.Extract(byName["orig.txt"])), Is.EqualTo("ORIG"));
    Assert.That(Encoding.ASCII.GetString(r.Extract(byName["a.bin"])), Is.EqualTo("ALPHA"));
    Assert.That(Encoding.ASCII.GetString(r.Extract(byName["b.bin"])), Is.EqualTo("BETA"));
    Assert.That(Encoding.ASCII.GetString(r.Extract(byName["c.bin"])), Is.EqualTo("GAMMA"));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_EmittedNewTrailer_PrevPointsAtOriginalXref() {
    var seed = BuildSeedPdf("seed.txt", "x"u8.ToArray());
    using var ms = IntoGrowableStream(seed);
    PdfInPlaceModifier.AddFile(ms, "added.txt", "y"u8.ToArray());

    var text = Encoding.Latin1.GetString(ms.ToArray());

    // The last "trailer" block must carry /Prev — that's the incremental-
    // update marker. The PDF written by PdfWriter has only one trailer
    // (no /Prev), so a /Prev in the final trailer proves the update path.
    var lastTrailer = text.LastIndexOf("trailer", StringComparison.Ordinal);
    Assert.That(lastTrailer, Is.GreaterThan(0));
    var lastEof = text.LastIndexOf("%%EOF", StringComparison.Ordinal);
    var trailerBlock = text[lastTrailer..lastEof];
    Assert.That(trailerBlock, Does.Contain("/Prev"));
  }

  // ── RemoveFiles ─────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void RemoveFiles_TombstonesEntry_ListingDrops() {
    var seed = BuildSeedPdf("kill.bin", "KILLME"u8.ToArray());
    using var ms = IntoGrowableStream(seed);
    PdfInPlaceModifier.AddFile(ms, "keep.bin", "STAY"u8.ToArray());

    var hits = PdfInPlaceModifier.RemoveFiles(ms, ["kill.bin"]);
    Assert.That(hits, Is.EqualTo(1));

    ms.Position = 0;
    var r = new PdfReader(ms);
    var names = r.Entries.Where(e => e.Filter == "EmbeddedFile").Select(e => e.Name).ToList();
    Assert.That(names, Does.Not.Contain("kill.bin"));
    Assert.That(names, Does.Contain("keep.bin"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFiles_OriginalObjectBytesSurvive_TombstoneOnly() {
    // The contract is "true in-place via xref free-list" — Remove must NOT
    // overwrite the original stream bytes. They survive in the file but are
    // tombstoned in the xref so spec-aware readers can't see them.
    var marker = "UNIQUE_MARKER_THAT_MUST_SURVIVE_REMOVE"u8.ToArray();
    var seed = BuildSeedPdf("victim.bin", marker);
    using var ms = IntoGrowableStream(seed);

    PdfInPlaceModifier.RemoveFiles(ms, ["victim.bin"]);

    var asAscii = Encoding.Latin1.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Contain("UNIQUE_MARKER_THAT_MUST_SURVIVE_REMOVE"),
      "Remove must tombstone via xref, NOT overwrite the original object bytes.");
  }

  [Test, Category("RoundTrip")]
  public void RemoveFiles_OriginalBytesPreservedByteIdentical_BeforeOldEof() {
    var seed = BuildSeedPdf("victim.bin", "GONESOON"u8.ToArray());
    using var ms = IntoGrowableStream(seed);

    PdfInPlaceModifier.RemoveFiles(ms, ["victim.bin"]);

    var after = ms.ToArray();
    var prefix = after.AsSpan(0, seed.Length).ToArray();
    Assert.That(prefix, Is.EqualTo(seed),
      "Remove appends a new xref subsection — every byte before the original %%EOF must stay byte-identical.");
  }

  [Test, Category("RoundTrip")]
  public void RemoveFiles_TombstoneXrefHasFreeEntry() {
    var seed = BuildSeedPdf("ghost.bin", "data"u8.ToArray());
    using var ms = IntoGrowableStream(seed);
    PdfInPlaceModifier.RemoveFiles(ms, ["ghost.bin"]);

    var text = Encoding.Latin1.GetString(ms.ToArray());
    var lastXref = text.LastIndexOf("\nxref\n", StringComparison.Ordinal);
    Assert.That(lastXref, Is.GreaterThan(0));
    var lastTrailer = text.IndexOf("\ntrailer", lastXref, StringComparison.Ordinal);
    var xrefBlock = text[lastXref..lastTrailer];
    // At least one ' f \n' entry must appear in the final xref subsection.
    Assert.That(xrefBlock, Does.Contain(" f \n"),
      "the appended xref subsection must mark at least one object as free.");
  }

  [Test, Category("RoundTrip")]
  public void RemoveFiles_MissingEntry_ReturnsZero() {
    var seed = BuildSeedPdf("only.bin", "x"u8.ToArray());
    using var ms = IntoGrowableStream(seed);
    var hits = PdfInPlaceModifier.RemoveFiles(ms, ["does-not-exist.bin"]);
    Assert.That(hits, Is.EqualTo(0));
  }

  [Test, Category("RoundTrip")]
  public void AddRemoveCycle_FinalListingCorrect() {
    var seed = BuildSeedPdf("a.bin", "A"u8.ToArray());
    using var ms = IntoGrowableStream(seed);
    PdfInPlaceModifier.AddFile(ms, "b.bin", "B"u8.ToArray());
    PdfInPlaceModifier.AddFile(ms, "c.bin", "C"u8.ToArray());
    PdfInPlaceModifier.RemoveFiles(ms, ["a.bin"]);
    PdfInPlaceModifier.AddFile(ms, "d.bin", "D"u8.ToArray());

    ms.Position = 0;
    var r = new PdfReader(ms);
    var names = r.Entries.Where(e => e.Filter == "EmbeddedFile").Select(e => e.Name).ToHashSet();
    Assert.That(names, Is.EquivalentTo(new[] { "b.bin", "c.bin", "d.bin" }));
  }

  // ── Descriptor wiring ───────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Descriptor_AdvertisesCanModify() {
    var d = new PdfFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_PathInPlace() {
    var seed = BuildSeedPdf("seed.txt", "SEED"u8.ToArray());
    using var ms = IntoGrowableStream(seed);
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "VIA-INTERFACE"u8.ToArray());
      ((IArchiveModifiable)new PdfFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via.txt", false)]);

      // Original bytes preserved.
      var after = ms.ToArray();
      var prefix = after.AsSpan(0, seed.Length).ToArray();
      Assert.That(prefix, Is.EqualTo(seed));

      // New entry readable.
      ms.Position = 0;
      var r = new PdfReader(ms);
      var added = r.Entries.Single(e => e.Filter == "EmbeddedFile" && e.Name == "via.txt");
      Assert.That(Encoding.ASCII.GetString(r.Extract(added)), Is.EqualTo("VIA-INTERFACE"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_TombstonesEntry() {
    var seed = BuildSeedPdf("die.txt", "X"u8.ToArray());
    using var ms = IntoGrowableStream(seed);
    PdfInPlaceModifier.AddFile(ms, "live.txt", "Y"u8.ToArray());

    ((IArchiveModifiable)new PdfFormatDescriptor()).Remove(ms, ["die.txt"]);

    ms.Position = 0;
    var r = new PdfReader(ms);
    var names = r.Entries.Where(e => e.Filter == "EmbeddedFile").Select(e => e.Name).ToList();
    Assert.That(names, Does.Not.Contain("die.txt"));
    Assert.That(names, Does.Contain("live.txt"));
  }

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_ContentsMatch() {
    var seed = BuildSeedPdf("alpha.bin", "AAA"u8.ToArray());
    using var ms = IntoGrowableStream(seed);
    PdfInPlaceModifier.AddFile(ms, "beta.bin", "BBB"u8.ToArray());

    var outDir = Path.Combine(Path.GetTempPath(), "pdf_inplace_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      ms.Position = 0;
      new PdfFormatDescriptor().Extract(ms, outDir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "alpha.bin")), Is.EqualTo("AAA"u8.ToArray()));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "beta.bin")), Is.EqualTo("BBB"u8.ToArray()));
    } finally {
      Directory.Delete(outDir, recursive: true);
    }
  }
}
