using Compression.Lib;
using Compression.Registry;
using FileFormat.Hog;

namespace Compression.Tests.Hog;

/// <summary>
/// In-place HOG modifier tests and byte-identity contract lock.
///
/// <para>HOG's record chain is naturally append-friendly: each entry is
/// {13-byte null-padded name + 4-byte LE size + size-byte data} and the
/// file has only a 3-byte "DHF" magic header. Add appends at EOF →
/// <c>[0, oldLength)</c> is byte-identical; Remove shifts the tail forward
/// by the removed-record size and truncates.</para>
/// </summary>
[TestFixture]
public class HogModifierTests {

  [OneTimeSetUp]
  public void EnsureRegistry() => FormatRegistration.EnsureInitialized();

  // ── Round-trip ─────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddFile_RoundTripsThroughReader() {
    var ms = BuildEmptyHog();
    HogModifier.AddFile(ms, "FIRST.TXT", "alpha"u8.ToArray());
    HogModifier.AddFile(ms, "SECOND.BIN", new byte[] { 1, 2, 3, 4, 5 });

    ms.Position = 0;
    var r = new HogReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].Name, Is.EqualTo("FIRST.TXT"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("alpha"u8.ToArray()));
    Assert.That(r.Entries[1].Name, Is.EqualTo("SECOND.BIN"));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_RoundTripsThroughReader() {
    var ms = BuildEmptyHog();
    HogModifier.AddFile(ms, "KEEP.TXT", "keep"u8.ToArray());
    HogModifier.AddFile(ms, "DROP.TXT", "drop me"u8.ToArray());
    HogModifier.AddFile(ms, "LAST.TXT", "last"u8.ToArray());

    var removed = HogModifier.RemoveFile(ms, "DROP.TXT");
    Assert.That(removed, Is.True);

    ms.Position = 0;
    var r = new HogReader(ms);
    Assert.That(r.Entries.Select(e => e.Name).ToArray(),
      Is.EqualTo(new[] { "KEEP.TXT", "LAST.TXT" }));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("keep"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("last"u8.ToArray()));
  }

  [Test, Category("Negative")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildEmptyHog();
    HogModifier.AddFile(ms, "EXISTS.TXT", "data"u8.ToArray());
    Assert.That(HogModifier.RemoveFile(ms, "GHOST.TXT"), Is.False);
  }

  // ── Byte-identity contract (the core PROMOTE lock) ────────────────

  [Test, Category("ContractLock")]
  public void HogModifier_AddFile_PrefixUnchanged_ByteIdentical() {
    var ms = BuildEmptyHog();
    HogModifier.AddFile(ms, "ORIGINAL.A", "alpha payload"u8.ToArray());
    HogModifier.AddFile(ms, "ORIGINAL.B", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
    var prefix = ms.ToArray();
    var prefixLength = prefix.Length;

    HogModifier.AddFile(ms, "NEW.C", "appended"u8.ToArray());

    ms.Position = 0;
    var after = ms.ToArray();
    Assert.That(after.Length, Is.GreaterThan(prefixLength),
      "Add must extend the stream (append-at-EOF)");
    Assert.That(after.AsSpan(0, prefixLength).SequenceEqual(prefix), Is.True,
      "Bytes [0, oldLength) must be byte-identical after HogModifier.AddFile " +
      "(append-only contract — that's the whole point of PROMOTE-ing HOG)");
  }

  [Test, Category("ContractLock")]
  public void HogModifier_AddFile_OnEmptyStream_StartsWithMagic() {
    using var ms = new MemoryStream();
    HogModifier.AddFile(ms, "FIRST.TXT", "hello"u8.ToArray());

    Assert.That(ms.Length, Is.GreaterThan(3));
    ms.Position = 0;
    Span<byte> magic = stackalloc byte[3];
    ms.ReadExactly(magic);
    Assert.That(magic.SequenceEqual("DHF"u8), Is.True,
      "Empty-stream Add must initialise the DHF magic at offset 0");
  }

  [Test, Category("ContractLock")]
  public void HogModifier_RemoveFile_TruncatesByExactRecordSize() {
    var ms = BuildEmptyHog();
    HogModifier.AddFile(ms, "KEEP.A", "keep"u8.ToArray());
    var oneFileLength = ms.Length;

    HogModifier.AddFile(ms, "DROP.B", "dropme"u8.ToArray());
    var twoFilesLength = ms.Length;

    HogModifier.RemoveFile(ms, "DROP.B");

    Assert.That(ms.Length, Is.EqualTo(oneFileLength),
      "Removing the most-recently-added entry must shrink the stream back to " +
      "exactly the prior length");
    Assert.That(ms.Length, Is.LessThan(twoFilesLength));
  }

  // ── Descriptor wiring ──────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsIArchiveModifiable() {
    var d = new HogFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That((d.Capabilities & FormatCapabilities.CanModify), Is.EqualTo(FormatCapabilities.CanModify),
      "HOG descriptor should advertise CanModify");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Add_AppendsFileToExistingArchive() {
    var ms = BuildEmptyHog();
    HogModifier.AddFile(ms, "EXISTING.TXT", "existing"u8.ToArray());

    var desc = new HogFormatDescriptor();
    var modifiable = (IArchiveModifiable)desc;

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "added"u8.ToArray());
      modifiable.Add(ms, [new ArchiveInputInfo(tmp, "ADDED.TXT", false)]);
    } finally {
      File.Delete(tmp);
    }

    ms.Position = 0;
    var entries = desc.List(ms, null);
    Assert.That(entries.Select(e => e.Name).ToArray(),
      Is.EqualTo(new[] { "EXISTING.TXT", "ADDED.TXT" }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Remove_DropsEntryFromExistingArchive() {
    var ms = BuildEmptyHog();
    HogModifier.AddFile(ms, "KEEP.TXT", "keep"u8.ToArray());
    HogModifier.AddFile(ms, "DROP.TXT", "drop"u8.ToArray());

    var desc = new HogFormatDescriptor();
    ((IArchiveModifiable)desc).Remove(ms, ["DROP.TXT"]);

    ms.Position = 0;
    var entries = desc.List(ms, null);
    Assert.That(entries.Select(e => e.Name).ToArray(),
      Is.EqualTo(new[] { "KEEP.TXT" }));
  }

  [Test, Category("Boundary")]
  public void HogModifier_AddFile_LongName_TruncatesTo13Bytes() {
    var ms = BuildEmptyHog();
    HogModifier.AddFile(ms, "VERY_LONG_NAME_TRUNC.DAT", "data"u8.ToArray());

    ms.Position = 0;
    var r = new HogReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name.Length, Is.LessThanOrEqualTo(13));
  }

  [Test, Category("Boundary")]
  public void HogModifier_AddFile_EmptyData_StillReadable() {
    var ms = BuildEmptyHog();
    HogModifier.AddFile(ms, "EMPTY.DAT", []);

    ms.Position = 0;
    var r = new HogReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(0));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(Array.Empty<byte>()));
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static MemoryStream BuildEmptyHog() {
    var ms = new MemoryStream();
    using (new HogWriter(ms, leaveOpen: true)) {
      // No files — just emits the 3-byte DHF magic via Finish().
    }
    ms.Position = 0;
    return ms;
  }
}
