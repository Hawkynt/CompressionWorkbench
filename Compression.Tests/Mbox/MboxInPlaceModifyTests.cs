#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Mbox;

namespace Compression.Tests.Mbox;

/// <summary>
/// Locks the contract that mbox Add/Remove are true in-place R/W: Append
/// only writes at EOF (every pre-existing byte byte-identical) and
/// Tombstone replaces a message record with a same-size
/// <c>X-Status: D</c> + zero-wiped tombstone (every other message's byte
/// offset unchanged).
/// </summary>
[TestFixture]
public class MboxInPlaceModifyTests {

  // ── Helpers ─────────────────────────────────────────────────────────────

  private static byte[] BuildMessage(string subject, string body) {
    var sb = new StringBuilder();
    sb.Append("From: sender@example.org\n");
    sb.Append("To: recipient@example.net\n");
    sb.Append("Subject: ").Append(subject).Append('\n');
    sb.Append("Date: Mon, 01 Jan 2024 00:00:00 +0000\n");
    sb.Append('\n');
    sb.Append(body);
    if (!body.EndsWith('\n')) sb.Append('\n');
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  private static byte[] BuildSeparator(string envelope, string asctime) =>
    Encoding.ASCII.GetBytes($"From {envelope} {asctime}\n");

  private static byte[] BuildRecord(string envelope, string asctime, string subject, string body) {
    var sep = BuildSeparator(envelope, asctime);
    var msg = BuildMessage(subject, body);
    var result = new byte[sep.Length + msg.Length];
    sep.CopyTo(result, 0);
    msg.CopyTo(result, sep.Length);
    return result;
  }

  private static MemoryStream IntoGrowableStream(byte[] bytes) {
    var ms = new MemoryStream(Math.Max(bytes.Length * 4, 256));
    ms.Write(bytes);
    ms.SetLength(bytes.Length);
    ms.Position = 0;
    return ms;
  }

  // ── Append ──────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Append_NewMessage_RoundTripsViaReader() {
    var seed = BuildRecord("alice@x.net", "Mon Jan  1 00:00:00 2024", "First", "Body one\n");
    using var ms = IntoGrowableStream(seed);

    var newMsg = BuildMessage("Second", "Second body\n");
    MboxInPlaceModifier.Append(ms, newMsg);

    ms.Position = 0;
    var entries = new MboxFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Does.Contain("First"));
    Assert.That(entries[1].Name, Does.Contain("Second"));
  }

  [Test, Category("RoundTrip")]
  public void Append_OriginalBytesPreservedByteIdentical() {
    var seed = BuildRecord("orig@x.net", "Mon Jan  1 00:00:00 2024", "Original", "Body\n");
    using var ms = IntoGrowableStream(seed);

    MboxInPlaceModifier.Append(ms, BuildMessage("New", "Hello\n"));

    var after = ms.ToArray();
    Assert.That(after.Length, Is.GreaterThan(seed.Length));
    var prefix = after.AsSpan(0, seed.Length).ToArray();
    Assert.That(prefix, Is.EqualTo(seed),
      "Append must only write at EOF; every pre-existing byte must stay byte-identical.");
  }

  [Test, Category("RoundTrip")]
  public void Append_ToEmptyMailbox_WritesSeparatorAndMessage() {
    using var ms = IntoGrowableStream([]);
    MboxInPlaceModifier.Append(ms, BuildMessage("Fresh", "Fresh body\n"));

    ms.Position = 0;
    var entries = new MboxFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Does.Contain("Fresh"));
  }

  [Test, Category("RoundTrip")]
  public void Append_MultipleTimes_AllMessagesReadable() {
    using var ms = IntoGrowableStream([]);
    MboxInPlaceModifier.Append(ms, BuildMessage("Alpha", "A\n"));
    MboxInPlaceModifier.Append(ms, BuildMessage("Beta", "B\n"));
    MboxInPlaceModifier.Append(ms, BuildMessage("Gamma", "G\n"));

    ms.Position = 0;
    var entries = new MboxFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(3));
    var subjects = entries.Select(e => e.Name).ToList();
    Assert.That(subjects[0], Does.Contain("Alpha"));
    Assert.That(subjects[1], Does.Contain("Beta"));
    Assert.That(subjects[2], Does.Contain("Gamma"));
  }

  [Test, Category("RoundTrip")]
  public void Append_FileNotEndingWithNewline_InjectsNewlineFirst() {
    // Build a corrupt seed (no trailing '\n') — Append must inject one so the
    // next "From " separator starts at a line boundary.
    var seedRec = BuildRecord("a@x.net", "Mon Jan  1 00:00:00 2024", "First", "no-newline");
    // Strip trailing '\n' (BuildMessage always adds one — drop the last byte).
    var seed = seedRec.AsSpan(0, seedRec.Length - 1).ToArray();
    using var ms = IntoGrowableStream(seed);
    MboxInPlaceModifier.Append(ms, BuildMessage("Next", "After\n"));

    ms.Position = 0;
    var entries = new MboxFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
  }

  // ── TombstoneAt ─────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void TombstoneAt_RemovesMessageFromListing() {
    using var ms = IntoGrowableStream([]);
    MboxInPlaceModifier.Append(ms, BuildMessage("Keep1", "k1\n"));
    MboxInPlaceModifier.Append(ms, BuildMessage("Drop", "drop me\n"));
    MboxInPlaceModifier.Append(ms, BuildMessage("Keep2", "k2\n"));

    var ok = MboxInPlaceModifier.TombstoneAt(ms, 1);
    Assert.That(ok, Is.True);

    ms.Position = 0;
    var entries = new MboxFormatDescriptor().List(ms, null);
    var subjects = entries.Select(e => e.Name).ToList();
    Assert.That(subjects.Any(s => s.Contains("Drop")), Is.False);
    Assert.That(subjects.Any(s => s.Contains("Keep1")), Is.True);
    Assert.That(subjects.Any(s => s.Contains("Keep2")), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void TombstoneAt_PreservesFileLength_SameSizeRecord() {
    using var ms = IntoGrowableStream([]);
    MboxInPlaceModifier.Append(ms, BuildMessage("First", "Body1\n"));
    MboxInPlaceModifier.Append(ms, BuildMessage("Second", "Body2\n"));
    var lenBefore = ms.Length;

    MboxInPlaceModifier.TombstoneAt(ms, 0);

    Assert.That(ms.Length, Is.EqualTo(lenBefore),
      "Tombstone must leave the record at its original size so every later message's byte offset is unchanged.");
  }

  [Test, Category("Security")]
  public void TombstoneAt_ZeroWipesOriginalBody() {
    using var ms = IntoGrowableStream([]);
    MboxInPlaceModifier.Append(ms, BuildMessage("Sensitive", "TOPSECRETXYZ_MBOX_MARKER\n"));
    MboxInPlaceModifier.TombstoneAt(ms, 0);

    var asAscii = Encoding.Latin1.GetString(ms.ToArray());
    Assert.That(asAscii, Does.Not.Contain("TOPSECRETXYZ_MBOX_MARKER"),
      "Tombstone must wipe the original body so deleted content is unrecoverable.");
  }

  [Test, Category("RoundTrip")]
  public void TombstoneAt_PreservesNeighboringMessageBytes() {
    using var ms = IntoGrowableStream([]);
    MboxInPlaceModifier.Append(ms, BuildMessage("Alpha", "alpha-body\n"));
    MboxInPlaceModifier.Append(ms, BuildMessage("Beta", "beta-body\n"));
    MboxInPlaceModifier.Append(ms, BuildMessage("Gamma", "gamma-body\n"));

    var bytesBefore = ms.ToArray();
    // Locate the Alpha and Gamma records.
    var ranges = MboxInPlaceModifier.FindMessageRanges(ms);
    Assert.That(ranges, Has.Count.EqualTo(3));

    MboxInPlaceModifier.TombstoneAt(ms, 1); // Beta

    var bytesAfter = ms.ToArray();
    // Alpha's bytes (range[0]) must stay byte-identical.
    var (aS, aE) = ranges[0];
    var alphaBefore = bytesBefore.AsSpan((int)aS, (int)(aE - aS)).ToArray();
    var alphaAfter = bytesAfter.AsSpan((int)aS, (int)(aE - aS)).ToArray();
    Assert.That(alphaAfter, Is.EqualTo(alphaBefore),
      "preceding message (Alpha) bytes must stay byte-identical.");

    // Gamma's bytes (range[2]) must stay byte-identical.
    var (gS, gE) = ranges[2];
    var gammaBefore = bytesBefore.AsSpan((int)gS, (int)(gE - gS)).ToArray();
    var gammaAfter = bytesAfter.AsSpan((int)gS, (int)(gE - gS)).ToArray();
    Assert.That(gammaAfter, Is.EqualTo(gammaBefore),
      "subsequent message (Gamma) bytes must stay byte-identical.");
  }

  [Test, Category("RoundTrip")]
  public void TombstoneAt_KeepsOtherMessagesReadable() {
    using var ms = IntoGrowableStream([]);
    MboxInPlaceModifier.Append(ms, BuildMessage("Subject1", "body1\n"));
    MboxInPlaceModifier.Append(ms, BuildMessage("Subject2", "body2\n"));
    MboxInPlaceModifier.Append(ms, BuildMessage("Subject3", "body3\n"));

    MboxInPlaceModifier.TombstoneAt(ms, 1);

    ms.Position = 0;
    var entries = new MboxFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries.Any(e => e.Name.Contains("Subject1")), Is.True);
    Assert.That(entries.Any(e => e.Name.Contains("Subject3")), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void TombstoneAt_OutOfRange_ReturnsFalse() {
    using var ms = IntoGrowableStream([]);
    MboxInPlaceModifier.Append(ms, BuildMessage("Only", "only\n"));
    Assert.That(MboxInPlaceModifier.TombstoneAt(ms, 99), Is.False);
  }

  // ── TombstoneBySubject ──────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void TombstoneBySubject_RemovesNamedMessage() {
    using var ms = IntoGrowableStream([]);
    MboxInPlaceModifier.Append(ms, BuildMessage("Keep", "k\n"));
    MboxInPlaceModifier.Append(ms, BuildMessage("Target", "t\n"));

    Assert.That(MboxInPlaceModifier.TombstoneBySubject(ms, "Target"), Is.True);

    ms.Position = 0;
    var entries = new MboxFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name.Contains("Target")), Is.False);
  }

  // ── Descriptor wiring ───────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Descriptor_AdvertisesCanModify() {
    var d = new MboxFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_AppendsAtEofByteIdentical() {
    using var seedStream = IntoGrowableStream([]);
    MboxInPlaceModifier.Append(seedStream, BuildMessage("Old", "old\n"));
    var seedSnap = seedStream.ToArray();

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, BuildMessage("Via", "via\n"));
      ((IArchiveModifiable)new MboxFormatDescriptor()).Add(seedStream,
        [new ArchiveInputInfo(tmp, "Via.eml", false)]);

      var after = seedStream.ToArray();
      Assert.That(after.Length, Is.GreaterThan(seedSnap.Length));
      var prefix = after.AsSpan(0, seedSnap.Length).ToArray();
      Assert.That(prefix, Is.EqualTo(seedSnap),
        "Descriptor.Add must route through the in-place modifier (byte-identical prefix).");

      seedStream.Position = 0;
      var entries = new MboxFormatDescriptor().List(seedStream, null);
      Assert.That(entries, Has.Count.EqualTo(2));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface_TombstonesByEntryName() {
    using var ms = IntoGrowableStream([]);
    MboxInPlaceModifier.Append(ms, BuildMessage("Stay", "s\n"));
    MboxInPlaceModifier.Append(ms, BuildMessage("Kill", "k\n"));

    var d = new MboxFormatDescriptor();
    ms.Position = 0;
    var entries = d.List(ms, null);
    var killName = entries.Single(e => e.Name.Contains("Kill")).Name;

    ms.Position = 0;
    ((IArchiveModifiable)d).Remove(ms, [killName]);

    ms.Position = 0;
    entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Does.Contain("Stay"));
  }

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_ContentsMatch() {
    using var ms = IntoGrowableStream([]);
    MboxInPlaceModifier.Append(ms, BuildMessage("A", "alpha-body\n"));
    MboxInPlaceModifier.Append(ms, BuildMessage("B", "beta-body\n"));
    MboxInPlaceModifier.TombstoneAt(ms, 0);

    var outDir = Path.Combine(Path.GetTempPath(), "mbox_inplace_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      ms.Position = 0;
      new MboxFormatDescriptor().Extract(ms, outDir, null, null);
      var produced = Directory.GetFiles(outDir);
      Assert.That(produced, Has.Length.EqualTo(1),
        "the tombstoned record must NOT be extracted.");
      var content = File.ReadAllText(produced[0]);
      Assert.That(content, Does.Contain("beta-body"),
        "the surviving message must extract verbatim.");
    } finally {
      Directory.Delete(outDir, recursive: true);
    }
  }
}
