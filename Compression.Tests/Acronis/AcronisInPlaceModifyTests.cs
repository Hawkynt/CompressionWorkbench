using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Acronis;

namespace Compression.Tests.Acronis;

/// <summary>
/// Locks the R-only → R/W (true in-place via record-stream append) promotion for the Acronis
/// classic .tib format. The on-disk invariant: every Add / Replace / Remove appends a fresh
/// record batch + a fresh EndTrailer + 12-byte fs trailer + 48-byte mirror footer at EOF, and
/// leaves <c>[0, oldLength)</c> byte-identical.
/// </summary>
[TestFixture]
public class AcronisInPlaceModifyTests {

  // ===== fixture builders (the canonical synthetic .tib slice for these tests) =====

  private sealed record TestFile(string Path, string Name, byte[] Content);

  private static byte[] BuildBaselineTib(IReadOnlyList<TestFile> testFiles) {
    using var ms = new MemoryStream();
    const int HeaderLength = 0x20;

    // 1) Volume header.
    Span<byte> hdr = stackalloc byte[HeaderLength];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr, 0xA2B924CEu);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[4..], HeaderLength);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[6..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[8..], 0x11111111);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[12..], 0x22222222);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[16..], 0x33333333);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[20..], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[24..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[28..], 32);
    ms.Write(hdr);

    var metaStart = (long)ms.Position;

    // 2) Per-file chains in archive order: 102 → 1 → 2 → 5 → 109 → 108 → Listing-after.
    // Mirrors AcronisInPlaceModifier's chain order (chain first, Listing last) so the fixture's
    // chain walk and the modifier's chain walk look identical to the reader.
    var ffmOffsets = new long[testFiles.Count];
    var blobOffsets = new long[testFiles.Count];
    var blobMd5s = new byte[testFiles.Count][];
    for (var i = 0; i < testFiles.Count; i++) {
      var f = testFiles[i];
      ffmOffsets[i] = ms.Position - HeaderLength;
      WriteRawDeflateRecord(ms, AcronisRecordType.FirstFileMetaRecord, BuildItemCommonBody(f.Name));
      WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaA, Encoding.ASCII.GetBytes($"meta1:{f.Name}"));
      WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaB, Encoding.ASCII.GetBytes($"meta2:{f.Name}"));
      WriteRawDeflateRecord(ms, AcronisRecordType.FileMetaC, Encoding.ASCII.GetBytes($"meta5:{f.Name}"));
      blobOffsets[i] = ms.Position;
      WriteZlibRecord(ms, AcronisRecordType.Blob, f.Content);
      blobMd5s[i] = MD5.HashData(f.Content);
      var idxPayload = BuildRecordIndexPayload(f.Content.LongLength,
        [(0L, blobOffsets[i] - HeaderLength, blobMd5s[i])]);
      WriteZlibRecord(ms, AcronisRecordType.RecordIndex, idxPayload);
    }

    // 3) Single Listing record (after the chains) — points each entry at its 102.
    var entries = new List<(string Path, string Name, long FileSize, long MetaOffset)>(testFiles.Count);
    for (var i = 0; i < testFiles.Count; i++)
      entries.Add((testFiles[i].Path, testFiles[i].Name, testFiles[i].Content.LongLength, ffmOffsets[i]));
    var listingPayload = BuildListingPayload(entries);
    WriteRawDeflateRecord(ms, AcronisRecordType.Listing, listingPayload);

    // 4) Closing trio: EndTrailer + 12-byte fs trailer + 48-byte mirror footer.
    ms.WriteByte((byte)AcronisRecordType.EndTrailer);
    Span<byte> trailer = stackalloc byte[12];
    BinaryPrimitives.WriteInt64LittleEndian(trailer, metaStart);
    trailer[8] = 0x2C; trailer[9] = 0x8A; trailer[10] = 0xE1; trailer[11] = 0x94;
    ms.Write(trailer);
    Span<byte> footer = stackalloc byte[48];
    BinaryPrimitives.WriteInt64LittleEndian(footer, ms.Length + 48);
    for (var i = 0; i < 32; i++) footer[16 + i] = hdr[31 - i];
    ms.Write(footer);

    return ms.ToArray();
  }

  private static void WriteRawDeflateRecord(Stream ms, AcronisRecordType type, byte[] payload) {
    ms.WriteByte((byte)type);
    using (var def = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
      def.Write(payload);
    Span<byte> sum = stackalloc byte[4];
    ms.Write(sum);
  }

  private static void WriteZlibRecord(Stream ms, AcronisRecordType type, byte[] payload) {
    ms.WriteByte((byte)type);
    ms.WriteByte(0x78);
    ms.WriteByte(0x9C);
    using (var def = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
      def.Write(payload);
    Span<byte> adlerBuf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(adlerBuf, Adler32(payload));
    ms.Write(adlerBuf);
  }

  private static uint Adler32(byte[] data) {
    const uint Mod = 65521;
    uint a = 1, b = 0;
    foreach (var x in data) { a = (a + x) % Mod; b = (b + a) % Mod; }
    return (b << 16) | a;
  }

  private static byte[] BuildItemCommonBody(string name) {
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);
    w.Write(1u);
    w.Write((uint)AcronisAttributeId.ItemCommon);
    var nameBytes = Encoding.Unicode.GetBytes(name);
    w.Write((ushort)(44 + nameBytes.Length));
    w.Write((ushort)name.Length);
    w.Write((ushort)0);
    w.Write(0u);
    w.Write(0UL); w.Write(0UL); w.Write(0UL); w.Write(0UL);
    w.Write(0u);
    if (nameBytes.Length > 0) w.Write(nameBytes);
    w.Flush();
    return ms.ToArray();
  }

  private static byte[] BuildListingPayload(IReadOnlyList<(string Path, string Name, long FileSize, long MetaOffset)> entries) {
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);
    w.Write((uint)entries.Count);
    foreach (var e in entries) {
      WriteCountedUtf16(w, e.Path);
      w.Write(0u);
      WriteCountedUtf16(w, e.Name);
      WriteCountedUtf16(w, "");
      WriteUInt48(w, 0); w.Write((ushort)0);
      w.Write(0u);
      WriteUInt48(w, (ulong)e.FileSize); w.Write((ushort)0);
      WriteUInt48(w, (ulong)e.FileSize); w.Write((ushort)0);
      WriteUInt48(w, (ulong)e.MetaOffset); w.Write((ushort)0);
      w.Write(new byte[38]);
    }
    w.Flush();
    return ms.ToArray();
  }

  private static byte[] BuildRecordIndexPayload(long totalSize, IReadOnlyList<(long startOffset, long recordOffset, byte[] md5)> handles) {
    using var ms = new MemoryStream();
    ms.Write([0x01, 0x02, 0x00, 0x10, 0x01, 0x00, 0x00, 0x00]);
    WriteUInt48Bytes(ms, (ulong)totalSize); ms.WriteByte(0); ms.WriteByte(0);
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)handles.Count);
    ms.Write(u32);
    foreach (var h in handles) {
      WriteUInt48Bytes(ms, (ulong)h.startOffset); ms.WriteByte(0); ms.WriteByte(0);
      WriteUInt48Bytes(ms, (ulong)h.recordOffset); ms.WriteByte(0); ms.WriteByte(0);
      ms.Write(h.md5);
    }
    return ms.ToArray();
  }

  private static void WriteCountedUtf16(BinaryWriter w, string s) {
    w.Write((uint)s.Length);
    if (s.Length > 0) w.Write(Encoding.Unicode.GetBytes(s));
  }

  private static void WriteUInt48(BinaryWriter w, ulong v) {
    for (var i = 0; i < 6; i++) w.Write((byte)((v >> (i * 8)) & 0xFF));
  }

  private static void WriteUInt48Bytes(Stream s, ulong v) {
    for (var i = 0; i < 6; i++) s.WriteByte((byte)((v >> (i * 8)) & 0xFF));
  }

  private static string FullNameOf(AcronisFileEntry e)
    => string.IsNullOrEmpty(e.Path) ? e.Name : e.Path.TrimEnd('/', '\\') + "/" + e.Name;

  // ===== Add =====

  [Test, Category("HappyPath")]
  public void Add_PreservesPriorBytes_NewEntryExtractableViaChainWalk() {
    var original = Encoding.ASCII.GetBytes("original content");
    var newBytes = Encoding.ASCII.GetBytes("freshly appended content");
    var baseline = BuildBaselineTib([new("d/", "old.txt", original)]);
    var oldLength = baseline.Length;

    using var image = new MemoryStream();
    image.Write(baseline);
    image.Position = 0;

    AcronisInPlaceModifier.Add(image, [ArchiveInputInfo.InMemory("d/new.txt", newBytes)]);

    var mutated = image.ToArray();
    Assert.Multiple(() => {
      Assert.That(mutated.Length, Is.GreaterThan(oldLength), "Add must grow the stream");
      Assert.That(mutated.AsSpan(0, oldLength).SequenceEqual(baseline),
        Is.True, "[0, oldLength) must be byte-identical to the baseline");
    });

    // Read back: both entries surface via the chain walk; new content matches.
    using var read = new MemoryStream(mutated, writable: false);
    var r = new AcronisReader(read);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    var oldEntry = r.Entries.Single(e => e.Name == "old.txt");
    var newEntry = r.Entries.Single(e => e.Name == "new.txt");
    Assert.Multiple(() => {
      Assert.That(oldEntry.FileSize, Is.EqualTo(original.LongLength));
      Assert.That(newEntry.FileSize, Is.EqualTo(newBytes.LongLength));
      Assert.That(r.ChainWalkComplete, Is.True, "chain walk must resolve every entry after Add");
    });

    var oldIdx = r.Entries.ToList().IndexOf(oldEntry);
    var newIdx = r.Entries.ToList().IndexOf(newEntry);
    var oldExtracted = r.ExtractFile(oldIdx);
    var newExtracted = r.ExtractFile(newIdx);
    Assert.Multiple(() => {
      Assert.That(oldExtracted.IntegrityValid, Is.True);
      Assert.That(oldExtracted.Data, Is.EqualTo(original));
      Assert.That(newExtracted.IntegrityValid, Is.True);
      Assert.That(newExtracted.Data, Is.EqualTo(newBytes));
    });
  }

  [Test, Category("HappyPath")]
  public void Add_MultipleFiles_AllRoundTrip() {
    var f1 = Encoding.UTF8.GetBytes("file one");
    var f2 = Encoding.UTF8.GetBytes("file two has more text");
    var baseline = BuildBaselineTib([new("", "zero.dat", [0xAA])]);

    using var image = new MemoryStream();
    image.Write(baseline);

    AcronisInPlaceModifier.Add(image, [
      ArchiveInputInfo.InMemory("d/a.txt", f1),
      ArchiveInputInfo.InMemory("d/b.txt", f2)
    ]);

    using var read = new MemoryStream(image.ToArray(), writable: false);
    var r = new AcronisReader(read);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.Multiple(() => {
      Assert.That(r.Entries.Single(e => e.Name == "a.txt").FileSize, Is.EqualTo(f1.LongLength));
      Assert.That(r.Entries.Single(e => e.Name == "b.txt").FileSize, Is.EqualTo(f2.LongLength));
    });
    var idxA = r.Entries.ToList().FindIndex(e => e.Name == "a.txt");
    var idxB = r.Entries.ToList().FindIndex(e => e.Name == "b.txt");
    Assert.Multiple(() => {
      Assert.That(r.ExtractFile(idxA).Data, Is.EqualTo(f1));
      Assert.That(r.ExtractFile(idxB).Data, Is.EqualTo(f2));
    });
  }

  [Test, Category("EdgeCase")]
  public void Add_EmptyInputs_LeavesImageByteIdentical() {
    var baseline = BuildBaselineTib([new("d/", "a.txt", Encoding.ASCII.GetBytes("x"))]);
    using var image = new MemoryStream();
    image.Write(baseline);
    AcronisInPlaceModifier.Add(image, []);
    Assert.That(image.ToArray(), Is.EqualTo(baseline));
  }

  // ===== Replace =====

  [Test, Category("HappyPath")]
  public void Replace_OldChainByteIdenticalAtOriginalOffsets_NewContentSurfaced() {
    var original = Encoding.ASCII.GetBytes("original content");
    var replacement = Encoding.ASCII.GetBytes("replacement is different and longer");
    var baseline = BuildBaselineTib([new("d/", "file.txt", original)]);
    var oldLength = baseline.Length;

    using var image = new MemoryStream();
    image.Write(baseline);
    image.Position = 0;

    AcronisInPlaceModifier.Replace(image, "d/file.txt", replacement);

    var mutated = image.ToArray();
    Assert.That(mutated.AsSpan(0, oldLength).SequenceEqual(baseline),
      Is.True, "[0, oldLength) must be byte-identical after Replace (old chain stays put)");

    using var read = new MemoryStream(mutated, writable: false);
    var r = new AcronisReader(read);
    Assert.That(r.Entries, Has.Count.EqualTo(1), "latest-Listing-wins must collapse to a single entry");
    var entry = r.Entries.Single();
    Assert.Multiple(() => {
      Assert.That(entry.Name, Is.EqualTo("file.txt"));
      Assert.That(entry.FileSize, Is.EqualTo(replacement.LongLength), "latest Listing carries new size");
    });

    var result = r.ExtractFile(0);
    Assert.Multiple(() => {
      Assert.That(result.IntegrityValid, Is.True);
      Assert.That(result.Data, Is.EqualTo(replacement),
        "Reader must surface the LATEST chain's content (not the old one)");
    });
  }

  [Test, Category("HappyPath")]
  public void Replace_SmallerContent_NewSizeAuthoritative() {
    var baseline = BuildBaselineTib([new("", "x.txt", Encoding.ASCII.GetBytes("longer original content"))]);
    using var image = new MemoryStream();
    image.Write(baseline);
    AcronisInPlaceModifier.Replace(image, "x.txt", Encoding.ASCII.GetBytes("smaller"));

    using var read = new MemoryStream(image.ToArray(), writable: false);
    var r = new AcronisReader(read);
    Assert.That(r.Entries.Single().FileSize, Is.EqualTo(7L));
    Assert.That(r.ExtractFile(0).Data, Is.EqualTo(Encoding.ASCII.GetBytes("smaller")));
  }

  // ===== Remove =====

  [Test, Category("HappyPath")]
  public void Remove_TombstoneAppended_OldChainIntact_EntryGoneFromReader() {
    var original = Encoding.ASCII.GetBytes("payload");
    var baseline = BuildBaselineTib([new("d/", "doomed.txt", original)]);
    var oldLength = baseline.Length;

    using var image = new MemoryStream();
    image.Write(baseline);
    image.Position = 0;

    AcronisInPlaceModifier.Remove(image, "d/doomed.txt");

    var mutated = image.ToArray();
    Assert.Multiple(() => {
      Assert.That(mutated.Length, Is.GreaterThan(oldLength), "Remove must grow the stream (tombstone is appended)");
      Assert.That(mutated.AsSpan(0, oldLength).SequenceEqual(baseline),
        Is.True, "[0, oldLength) must be byte-identical after Remove (old chain stays put)");
    });

    using var read = new MemoryStream(mutated, writable: false);
    var r = new AcronisReader(read);
    Assert.That(r.Entries, Is.Empty, "tombstoned entry must vanish from the live entry view");
  }

  [Test, Category("HappyPath")]
  public void Remove_KeepsUntouchedEntries_OnlyTargetedNameGoes() {
    var baseline = BuildBaselineTib([
      new("", "a.txt", Encoding.ASCII.GetBytes("alpha")),
      new("", "b.txt", Encoding.ASCII.GetBytes("beta")),
      new("", "c.txt", Encoding.ASCII.GetBytes("gamma")),
    ]);

    using var image = new MemoryStream();
    image.Write(baseline);
    AcronisInPlaceModifier.Remove(image, "b.txt");

    using var read = new MemoryStream(image.ToArray(), writable: false);
    var r = new AcronisReader(read);
    var names = r.Entries.Select(e => e.Name).OrderBy(n => n).ToArray();
    Assert.That(names, Is.EqualTo(new[] { "a.txt", "c.txt" }));
  }

  [Test, Category("HappyPath")]
  public void Remove_ThenAddSameName_Resurrects() {
    var baseline = BuildBaselineTib([new("", "f.txt", Encoding.ASCII.GetBytes("v1"))]);
    using var image = new MemoryStream();
    image.Write(baseline);
    AcronisInPlaceModifier.Remove(image, "f.txt");

    using (var afterRemove = new MemoryStream(image.ToArray(), writable: false)) {
      Assert.That(new AcronisReader(afterRemove).Entries, Is.Empty);
    }

    AcronisInPlaceModifier.Add(image, [ArchiveInputInfo.InMemory("f.txt", Encoding.ASCII.GetBytes("v2"))]);
    using var read = new MemoryStream(image.ToArray(), writable: false);
    var r = new AcronisReader(read);
    Assert.That(r.Entries.Single().Name, Is.EqualTo("f.txt"));
    Assert.That(r.ExtractFile(0).Data, Is.EqualTo(Encoding.ASCII.GetBytes("v2")));
  }

  // ===== Mutate-then-extract round-trip =====

  [Test, Category("HappyPath")]
  public void MutationCascade_AddReplaceRemove_RoundTripsCorrectFinalState() {
    var baseline = BuildBaselineTib([
      new("", "keep.txt", Encoding.ASCII.GetBytes("keep me")),
      new("", "edit.txt", Encoding.ASCII.GetBytes("v1")),
      new("", "drop.txt", Encoding.ASCII.GetBytes("temporary")),
    ]);
    using var image = new MemoryStream();
    image.Write(baseline);

    AcronisInPlaceModifier.Add(image, [ArchiveInputInfo.InMemory("added.txt", Encoding.ASCII.GetBytes("newcomer"))]);
    AcronisInPlaceModifier.Replace(image, "edit.txt", Encoding.ASCII.GetBytes("v2 is the new edit"));
    AcronisInPlaceModifier.Remove(image, "drop.txt");

    using var read = new MemoryStream(image.ToArray(), writable: false);
    var r = new AcronisReader(read);
    var names = r.Entries.Select(FullNameOf).OrderBy(n => n).ToArray();
    Assert.That(names, Is.EqualTo(new[] { "added.txt", "edit.txt", "keep.txt" }));

    string Extract(string name) {
      var idx = r.Entries.ToList().FindIndex(e => FullNameOf(e) == name);
      return Encoding.ASCII.GetString(r.ExtractFile(idx).Data);
    }

    Assert.Multiple(() => {
      Assert.That(Extract("keep.txt"), Is.EqualTo("keep me"));
      Assert.That(Extract("edit.txt"), Is.EqualTo("v2 is the new edit"));
      Assert.That(Extract("added.txt"), Is.EqualTo("newcomer"));
    });
  }

  // ===== Reader-level invariants =====

  [Test, Category("HappyPath")]
  public void Walker_SkipsEmbeddedTrailerBlocks_AfterTwoSuccessiveAdds() {
    var baseline = BuildBaselineTib([new("", "first.txt", Encoding.ASCII.GetBytes("a"))]);
    using var image = new MemoryStream();
    image.Write(baseline);

    AcronisInPlaceModifier.Add(image, [ArchiveInputInfo.InMemory("second.txt", Encoding.ASCII.GetBytes("b"))]);
    AcronisInPlaceModifier.Add(image, [ArchiveInputInfo.InMemory("third.txt", Encoding.ASCII.GetBytes("c"))]);

    using var read = new MemoryStream(image.ToArray(), writable: false);
    var r = new AcronisReader(read);
    var names = r.Entries.Select(e => e.Name).OrderBy(n => n).ToArray();
    Assert.That(names, Is.EqualTo(new[] { "first.txt", "second.txt", "third.txt" }));
  }

  // ===== Descriptor wiring =====

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify_AndImplementsIArchiveModifiable() {
    var desc = new AcronisFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(desc, Is.InstanceOf<IArchiveModifiable>());
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Add_RoutesThroughInPlaceModifier() {
    var baseline = BuildBaselineTib([new("", "old.txt", Encoding.ASCII.GetBytes("old"))]);
    var oldLength = baseline.Length;
    using var image = new MemoryStream();
    image.Write(baseline);

    var desc = new AcronisFormatDescriptor();
    desc.Add(image, [ArchiveInputInfo.InMemory("brand-new.txt", Encoding.ASCII.GetBytes("brand new bytes"))]);

    var mutated = image.ToArray();
    Assert.That(mutated.AsSpan(0, oldLength).SequenceEqual(baseline), Is.True);
    using var read = new MemoryStream(mutated, writable: false);
    var r = new AcronisReader(read);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AddDuplicateName_ReplacesViaInPlaceModifier() {
    var baseline = BuildBaselineTib([new("", "dup.txt", Encoding.ASCII.GetBytes("v1"))]);
    using var image = new MemoryStream();
    image.Write(baseline);

    var desc = new AcronisFormatDescriptor();
    desc.Add(image, [ArchiveInputInfo.InMemory("dup.txt", Encoding.ASCII.GetBytes("v2 replaces v1"))]);

    using var read = new MemoryStream(image.ToArray(), writable: false);
    var r = new AcronisReader(read);
    Assert.That(r.Entries.Single().Name, Is.EqualTo("dup.txt"));
    Assert.That(r.ExtractFile(0).Data, Is.EqualTo(Encoding.ASCII.GetBytes("v2 replaces v1")));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Remove_DelegatesToInPlaceModifier_DropsEntry() {
    var baseline = BuildBaselineTib([
      new("", "stay.txt", Encoding.ASCII.GetBytes("stay")),
      new("", "go.txt", Encoding.ASCII.GetBytes("go")),
    ]);
    using var image = new MemoryStream();
    image.Write(baseline);

    var desc = new AcronisFormatDescriptor();
    desc.Remove(image, ["go.txt"]);

    using var read = new MemoryStream(image.ToArray(), writable: false);
    var r = new AcronisReader(read);
    Assert.That(r.Entries.Single().Name, Is.EqualTo("stay.txt"));
  }

  // ===== Error / argument-validation =====

  [Test, Category("ErrorHandling")]
  public void Add_NullStream_Throws() {
    Assert.Throws<ArgumentNullException>(() => AcronisInPlaceModifier.Add(null!, []));
  }

  [Test, Category("ErrorHandling")]
  public void Add_NullInputs_Throws() {
    using var ms = new MemoryStream();
    Assert.Throws<ArgumentNullException>(() => AcronisInPlaceModifier.Add(ms, null!));
  }

  [Test, Category("ErrorHandling")]
  public void Replace_NullStream_Throws() {
    Assert.Throws<ArgumentNullException>(() => AcronisInPlaceModifier.Replace(null!, "x", []));
  }

  [Test, Category("ErrorHandling")]
  public void Remove_NullStream_Throws() {
    Assert.Throws<ArgumentNullException>(() => AcronisInPlaceModifier.Remove(null!, "x"));
  }

  [Test, Category("ErrorHandling")]
  public void Modify_NonSeekableStream_Throws() {
    // A non-seekable forwarding wrapper over a MemoryStream.
    using var inner = new MemoryStream();
    using var nonSeek = new NonSeekableForwarder(inner);
    Assert.Throws<ArgumentException>(() => AcronisInPlaceModifier.Add(nonSeek, [ArchiveInputInfo.InMemory("x", [1])]));
    Assert.Throws<ArgumentException>(() => AcronisInPlaceModifier.Replace(nonSeek, "x", [1]));
    Assert.Throws<ArgumentException>(() => AcronisInPlaceModifier.Remove(nonSeek, "x"));
  }

  private sealed class NonSeekableForwarder : Stream {
    private readonly Stream _inner;
    public NonSeekableForwarder(Stream inner) { this._inner = inner; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => this._inner.Length;
    public override long Position { get => this._inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => this._inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => this._inner.Write(buffer, offset, count);
  }
}
