using Compression.Lib;
using Compression.Registry;
using FileFormat.Wad2;

namespace Compression.Tests.Wad2;

/// <summary>
/// In-place WAD2/WAD3 modifier tests and byte-identity contract lock.
///
/// <para>WAD's directory lives at the END of the file (pointed to by the
/// 12-byte header's <c>dirOffset</c> field). Add truncates the trailing
/// directory, appends new entry data, and re-emits the directory. The
/// 4-byte magic at <c>[0, 4)</c> and the data region <c>[12, oldDirOffset)</c>
/// must survive byte-identical — no pre-existing entry's data moves.</para>
/// </summary>
[TestFixture]
public class Wad2ModifierTests {

  [OneTimeSetUp]
  public void EnsureRegistry() => FormatRegistration.EnsureInitialized();

  private const int HeaderSize = 12;

  // ── Round-trip ─────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AddEntry_RoundTripsThroughReader() {
    var ms = BuildEmptyWad();
    Wad2Modifier.AddEntry(ms, "TEX_A", "alpha"u8.ToArray());
    Wad2Modifier.AddEntry(ms, "TEX_B", new byte[] { 1, 2, 3, 4, 5 });

    ms.Position = 0;
    var r = new Wad2Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].Name, Is.EqualTo("TEX_A"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("alpha"u8.ToArray()));
    Assert.That(r.Entries[1].Name, Is.EqualTo("TEX_B"));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
  }

  [Test, Category("RoundTrip")]
  public void RemoveEntry_RoundTripsThroughReader() {
    var ms = BuildEmptyWad();
    Wad2Modifier.AddEntry(ms, "KEEP_A", "keep"u8.ToArray());
    Wad2Modifier.AddEntry(ms, "DROP_B", "drop"u8.ToArray());
    Wad2Modifier.AddEntry(ms, "KEEP_C", "kept"u8.ToArray());

    Assert.That(Wad2Modifier.RemoveEntry(ms, "DROP_B"), Is.True);

    ms.Position = 0;
    var r = new Wad2Reader(ms);
    Assert.That(r.Entries.Select(e => e.Name).ToArray(),
      Is.EqualTo(new[] { "KEEP_A", "KEEP_C" }));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("keep"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("kept"u8.ToArray()));
  }

  [Test, Category("Negative")]
  public void RemoveEntry_NotFound_ReturnsFalse() {
    var ms = BuildEmptyWad();
    Wad2Modifier.AddEntry(ms, "EXISTS", "data"u8.ToArray());
    Assert.That(Wad2Modifier.RemoveEntry(ms, "GHOST"), Is.False);
  }

  // ── Byte-identity contract (the core PROMOTE lock) ────────────────

  [Test, Category("ContractLock")]
  public void Wad2Modifier_AddEntry_MagicSurvives_ByteIdentical() {
    var ms = BuildEmptyWad();
    Wad2Modifier.AddEntry(ms, "EXISTING", "alpha"u8.ToArray());
    var preMagic = ReadAt(ms, 0, 4);

    Wad2Modifier.AddEntry(ms, "APPENDED", "beta"u8.ToArray());

    var postMagic = ReadAt(ms, 0, 4);
    Assert.That(postMagic, Is.EqualTo(preMagic),
      "WAD2/WAD3 magic at [0, 4) must survive byte-identical");
  }

  [Test, Category("ContractLock")]
  public void Wad2Modifier_AddEntry_PreExistingDataRegion_ByteIdentical() {
    var ms = BuildEmptyWad();
    Wad2Modifier.AddEntry(ms, "EX_A", new byte[] { 0xCA, 0xFE, 0xBA, 0xBE });
    Wad2Modifier.AddEntry(ms, "EX_B", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

    // Snapshot data region [HeaderSize, oldDirOffset).
    var oldDirOffset = ReadDirOffset(ms);
    var oldDataRegion = ReadAt(ms, HeaderSize, (int)(oldDirOffset - HeaderSize));

    Wad2Modifier.AddEntry(ms, "EX_C", new byte[] { 0x11, 0x22, 0x33 });

    // Pre-existing data bytes must be byte-identical after Add.
    var postDataRegion = ReadAt(ms, HeaderSize, oldDataRegion.Length);
    Assert.That(postDataRegion, Is.EqualTo(oldDataRegion),
      "Data region [HeaderSize, oldDirOffset) must survive byte-identical after AddEntry");
  }

  [Test, Category("ContractLock")]
  public void Wad2Modifier_AddEntry_HeaderNumEntriesAndDirOffset_BothUpdated() {
    var ms = BuildEmptyWad();
    Wad2Modifier.AddEntry(ms, "FIRST", "alpha"u8.ToArray());
    var preDirOffset = ReadDirOffset(ms);
    var preNumEntries = ReadNumEntries(ms);
    Assert.That(preNumEntries, Is.EqualTo(1u));

    Wad2Modifier.AddEntry(ms, "SECOND", "beta"u8.ToArray());

    var postDirOffset = ReadDirOffset(ms);
    var postNumEntries = ReadNumEntries(ms);
    Assert.That(postNumEntries, Is.EqualTo(2u),
      "Header numEntries must be patched to reflect the new entry");
    Assert.That(postDirOffset, Is.GreaterThan(preDirOffset),
      "Header dirOffset must advance to the new data EOF after Add");
  }

  // ── Descriptor wiring ──────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsIArchiveModifiable() {
    var d = new Wad2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That((d.Capabilities & FormatCapabilities.CanModify), Is.EqualTo(FormatCapabilities.CanModify),
      "WAD2 descriptor should advertise CanModify");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Add_AppendsEntryToExistingArchive() {
    var ms = BuildEmptyWad();
    Wad2Modifier.AddEntry(ms, "EXISTING", "existing"u8.ToArray());

    var desc = new Wad2FormatDescriptor();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "appended"u8.ToArray());
      ((IArchiveModifiable)desc).Add(ms, [new ArchiveInputInfo(tmp, "APPENDED", false)]);
    } finally {
      File.Delete(tmp);
    }

    ms.Position = 0;
    var entries = desc.List(ms, null);
    Assert.That(entries.Select(e => e.Name).ToArray(),
      Is.EqualTo(new[] { "EXISTING", "APPENDED" }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Remove_DropsEntryFromExistingArchive() {
    var ms = BuildEmptyWad();
    Wad2Modifier.AddEntry(ms, "KEEP", "keep"u8.ToArray());
    Wad2Modifier.AddEntry(ms, "DROP", "drop"u8.ToArray());

    var desc = new Wad2FormatDescriptor();
    ((IArchiveModifiable)desc).Remove(ms, ["DROP"]);

    ms.Position = 0;
    var entries = desc.List(ms, null);
    Assert.That(entries.Select(e => e.Name).ToArray(),
      Is.EqualTo(new[] { "KEEP" }));
  }

  [Test, Category("Boundary")]
  public void Wad2Modifier_AddEntry_LongName_TruncatesTo16Bytes() {
    var ms = BuildEmptyWad();
    Wad2Modifier.AddEntry(ms, "VERY_LONG_NAME_PAST_16", "data"u8.ToArray());

    ms.Position = 0;
    var r = new Wad2Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name.Length, Is.LessThanOrEqualTo(16));
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static byte[] ReadAt(Stream s, long offset, int length) {
    var prev = s.Position;
    try {
      s.Position = offset;
      var buf = new byte[length];
      s.ReadExactly(buf);
      return buf;
    } finally {
      s.Position = prev;
    }
  }

  private static uint ReadNumEntries(Stream s) {
    var bytes = ReadAt(s, 4, 4);
    return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
  }

  private static uint ReadDirOffset(Stream s) {
    var bytes = ReadAt(s, 8, 4);
    return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
  }

  private static MemoryStream BuildEmptyWad() {
    var ms = new MemoryStream();
    using (new Wad2Writer(ms, leaveOpen: true)) {
      // No entries — emits 12-byte header with 0 entries and dirOffset=12.
    }
    ms.Position = 0;
    return ms;
  }
}
