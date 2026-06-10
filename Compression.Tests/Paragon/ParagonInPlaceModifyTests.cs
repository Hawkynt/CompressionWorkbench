using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Paragon;

namespace Compression.Tests.Paragon;

/// <summary>
/// Locks in the WORM → R/W promotion via CWBP chunk-table append. Every
/// test asserts the byte-preservation invariant: the chunk-body region
/// of the original image (<c>[ParagonWriter.HeaderSize, oldChunkTableOffset)</c>)
/// stays byte-identical after Add / Replace / Remove, and the per-chunk
/// Adler-32 still validates on every untouched chunk.
/// </summary>
[TestFixture]
public class ParagonInPlaceModifyTests {

  /// <summary>
  /// Emits a fresh CWBP-discriminated PBF carrying <paramref name="chunks"/>
  /// as one chunk per element. Returns the image bytes plus the
  /// chunk-table-offset they were written at — tests use the latter as
  /// the byte-preservation boundary.
  /// </summary>
  private static (byte[] Bytes, long OldChunkTableOffset) BuildImage(IReadOnlyList<byte[]> chunks, bool compressChunks = false) {
    using var ms = new MemoryStream();
    using (var w = new ParagonWriter(ms, compressChunks: compressChunks, leaveOpen: true)) {
      foreach (var c in chunks)
        w.WriteChunk(c);
      w.Finalise();
    }
    var bytes = ms.ToArray();
    // The chunk-table offset is the field we patch at +0x104 — read it
    // back from the canonical position so the test mirrors the wire spec.
    var tableOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(ParagonWriter.OffsetChunkTableOffset, 8));
    return (bytes, (long)tableOffset);
  }

  private static MemoryStream OpenRw(byte[] image) {
    // Expandable backing buffer so SetLength on Add/Replace can grow.
    var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);
    ms.Position = 0;
    return ms;
  }

  // ── Add ─────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_PreservesAllPreviousChunkBodyBytes_ByteIdentical() {
    var chunkA = Encoding.UTF8.GetBytes("alpha chunk body");
    var chunkB = Encoding.UTF8.GetBytes("beta chunk body");
    var (image, oldTableOffset) = BuildImage([chunkA, chunkB]);

    using var stream = OpenRw(image);
    ParagonInPlaceModifier.AddChunks(stream, [
      ArchiveInputInfo.InMemory("gamma.bin", Encoding.UTF8.GetBytes("gamma added body")),
    ], compressChunks: false);

    var mutated = stream.ToArray();
    Assert.That(mutated.Length, Is.GreaterThan(image.Length),
      "Add must grow the image.");
    // The chunk-body region [HeaderSize, oldChunkTableOffset) must stay
    // byte-identical: every existing chunk's body bytes at their original
    // file offsets unchanged. The TOC fields at +0x100 / +0x104 / +0x114
    // are patched by design (ChunkCount, ChunkTableOffset, TotalLogicalSize).
    var bodyRegionLen = (int)oldTableOffset - ParagonWriter.HeaderSize;
    Assert.That(mutated.AsSpan(ParagonWriter.HeaderSize, bodyRegionLen).ToArray(),
      Is.EqualTo(image.AsSpan(ParagonWriter.HeaderSize, bodyRegionLen).ToArray()),
      "Every byte in [HeaderSize, oldChunkTableOffset) — every existing chunk body — must stay byte-identical after Add.");
    // The magic + CWBP discriminator stay byte-identical too.
    Assert.That(mutated.AsSpan(0, 8).ToArray(), Is.EqualTo(image.AsSpan(0, 8).ToArray()),
      "Magic + Major + FormatVersion must stay byte-identical.");
    Assert.That(mutated.AsSpan(ParagonWriter.OffsetCwbpDiscriminator, 8).ToArray(),
      Is.EqualTo(image.AsSpan(ParagonWriter.OffsetCwbpDiscriminator, 8).ToArray()),
      "CWBP discriminator must stay byte-identical.");
  }

  [Test, Category("HappyPath")]
  public void Add_ReaderSurfacesAllPreExistingAndNewChunks() {
    var chunkA = Encoding.UTF8.GetBytes("alpha");
    var chunkB = Encoding.UTF8.GetBytes("beta");
    var (image, _) = BuildImage([chunkA, chunkB]);

    using var stream = OpenRw(image);
    ParagonInPlaceModifier.AddChunks(stream, [
      ArchiveInputInfo.InMemory("c1.bin", Encoding.UTF8.GetBytes("gamma")),
      ArchiveInputInfo.InMemory("c2.bin", Encoding.UTF8.GetBytes("delta")),
    ], compressChunks: true);

    stream.Position = 0;
    using var r = new ParagonReader(stream);
    Assert.That(r.IsCwbpProduced, Is.True);
    Assert.That(r.ChunkCount, Is.EqualTo(4u),
      "After Add the on-disk table carries the union of pre-existing + appended entries.");

    var liveChunks = r.Entries.Where(e => e.Name.StartsWith("chunk_")).ToList();
    Assert.That(liveChunks.Select(e => e.Name).ToList(), Is.EquivalentTo(new[] {
      "chunk_000000.bin", "chunk_000001.bin", "chunk_000002.bin", "chunk_000003.bin",
    }));
    Assert.That(liveChunks.First(e => e.Name == "chunk_000000.bin").Data, Is.EqualTo(chunkA));
    Assert.That(liveChunks.First(e => e.Name == "chunk_000001.bin").Data, Is.EqualTo(chunkB));
    Assert.That(liveChunks.First(e => e.Name == "chunk_000002.bin").Data, Is.EqualTo(Encoding.UTF8.GetBytes("gamma")));
    Assert.That(liveChunks.First(e => e.Name == "chunk_000003.bin").Data, Is.EqualTo(Encoding.UTF8.GetBytes("delta")));
  }

  [Test, Category("HappyPath")]
  public void Add_ExistingChunksAdler32_StillValidates() {
    // Adler-32 verification runs inside ParagonReader.ExtractChunkBytes
    // and throws on mismatch. Successfully reading every pre-existing
    // chunk proves their bodies (and table entries' Adler-32 fields)
    // round-trip across the append.
    var bytesA = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
    var bytesB = new byte[1024];
    new Random(13).NextBytes(bytesB);
    var (image, _) = BuildImage([bytesA, bytesB], compressChunks: true);

    using var stream = OpenRw(image);
    ParagonInPlaceModifier.AddChunks(stream, [
      ArchiveInputInfo.InMemory("new.bin", new byte[] { 1, 2, 3, 4 }),
    ], compressChunks: true);

    stream.Position = 0;
    using var r = new ParagonReader(stream);
    Assert.DoesNotThrow(() => {
      var _ = r.AssembleLogicalPayload();
    }, "Adler-32 on every pre-existing chunk must still validate after Add.");
  }

  [Test, Category("HappyPath")]
  public void Add_OnEmptyImage_ProducesSingleChunkImage() {
    // The empty-image base case — no existing chunks, Add lays down the
    // first one. Exercises the maxChunkNumber=hasAny=false branch.
    using var ms = new MemoryStream();
    using (var w = new ParagonWriter(ms, compressChunks: false, leaveOpen: true))
      w.Finalise();
    var image = ms.ToArray();

    using var stream = OpenRw(image);
    ParagonInPlaceModifier.AddChunks(stream, [
      ArchiveInputInfo.InMemory("first.bin", Encoding.UTF8.GetBytes("first content")),
    ], compressChunks: false);

    stream.Position = 0;
    using var r = new ParagonReader(stream);
    Assert.That(r.ChunkCount, Is.EqualTo(1u));
    var entry = r.Entries.Single(e => e.Name == "chunk_000000.bin");
    Assert.That(entry.Data, Is.EqualTo(Encoding.UTF8.GetBytes("first content")));
  }

  // ── Replace ──────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Replace_KeepsOldChunkBodyBytes_ByteIdenticalAtOriginalOffset() {
    var chunkA = Encoding.UTF8.GetBytes("alpha-original-body");
    var chunkB = Encoding.UTF8.GetBytes("beta-original-body");
    var (image, oldTableOffset) = BuildImage([chunkA, chunkB], compressChunks: false);

    using var stream = OpenRw(image);
    ParagonInPlaceModifier.ReplaceChunk(stream, "chunk_000000.bin",
      Encoding.UTF8.GetBytes("alpha-REPLACED-body"), compressChunks: false);

    var mutated = stream.ToArray();
    // The chunk-body region [HeaderSize, oldChunkTableOffset) must stay
    // byte-identical: every existing chunk body — including the one
    // being replaced — is still on disk at its original offset. TOC
    // fields at +0x100/+0x104/+0x114 are patched by design.
    var bodyRegionLen = (int)oldTableOffset - ParagonWriter.HeaderSize;
    Assert.That(mutated.AsSpan(ParagonWriter.HeaderSize, bodyRegionLen).ToArray(),
      Is.EqualTo(image.AsSpan(ParagonWriter.HeaderSize, bodyRegionLen).ToArray()),
      "Replace must preserve all chunk-body bytes in [HeaderSize, oldChunkTableOffset) byte-identical — the old chunk is still on disk at its original offset.");
  }

  [Test, Category("HappyPath")]
  public void Replace_ReaderReturnsNewContent_NotOriginal() {
    var (image, _) = BuildImage([
      Encoding.UTF8.GetBytes("alpha-original"),
      Encoding.UTF8.GetBytes("beta-original"),
    ], compressChunks: true);

    using var stream = OpenRw(image);
    ParagonInPlaceModifier.ReplaceChunk(stream, "chunk_000001.bin",
      Encoding.UTF8.GetBytes("beta-LATEST-WINS"), compressChunks: true);

    stream.Position = 0;
    using var r = new ParagonReader(stream);
    var liveBeta = r.Entries.Single(e => e.Name == "chunk_000001.bin");
    Assert.That(liveBeta.Data, Is.EqualTo(Encoding.UTF8.GetBytes("beta-LATEST-WINS")),
      "Reader's latest-wins-per-chunk-number must surface the new content for the replaced chunk.");
    var liveAlpha = r.Entries.Single(e => e.Name == "chunk_000000.bin");
    Assert.That(liveAlpha.Data, Is.EqualTo(Encoding.UTF8.GetBytes("alpha-original")),
      "Unreplaced chunks must still surface their original content.");
  }

  [Test, Category("HappyPath")]
  public void Replace_OnDiskTableCarriesBothOldAndNewEntryForSameChunkNumber() {
    var (image, _) = BuildImage([
      Encoding.UTF8.GetBytes("alpha"),
      Encoding.UTF8.GetBytes("beta"),
    ], compressChunks: false);

    using var stream = OpenRw(image);
    ParagonInPlaceModifier.ReplaceChunk(stream, "chunk_000000.bin",
      Encoding.UTF8.GetBytes("alpha-NEW"), compressChunks: false);

    stream.Position = 0;
    using var r = new ParagonReader(stream);
    Assert.That(r.ChunkCount, Is.EqualTo(3u),
      "Replace appends a new entry — on-disk count grows by 1.");
    var entriesForZero = r.ChunkTable.Where(c => c.ChunkNumber == 0u).ToList();
    Assert.That(entriesForZero, Has.Count.EqualTo(2),
      "Both the original and the replacement entries must be on disk, sharing ChunkNumber 0.");
  }

  [Test, Category("Sad")]
  public void Replace_UnknownChunk_ThrowsFileNotFound() {
    var (image, _) = BuildImage([Encoding.UTF8.GetBytes("only")]);
    using var stream = OpenRw(image);
    Assert.Throws<FileNotFoundException>(() =>
      ParagonInPlaceModifier.ReplaceChunk(stream, "chunk_000005.bin", new byte[] { 1, 2 }));
  }

  // ── Remove ──────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_KeepsOriginalBodyBytes_ByteIdentical() {
    var chunkA = Encoding.UTF8.GetBytes("alpha-survives");
    var chunkB = Encoding.UTF8.GetBytes("beta-tombstoned");
    var (image, oldTableOffset) = BuildImage([chunkA, chunkB], compressChunks: false);

    using var stream = OpenRw(image);
    ParagonInPlaceModifier.RemoveChunk(stream, "chunk_000001.bin");

    var mutated = stream.ToArray();
    var bodyRegionLen = (int)oldTableOffset - ParagonWriter.HeaderSize;
    Assert.That(mutated.AsSpan(ParagonWriter.HeaderSize, bodyRegionLen).ToArray(),
      Is.EqualTo(image.AsSpan(ParagonWriter.HeaderSize, bodyRegionLen).ToArray()),
      "Remove must preserve all chunk-body bytes in [HeaderSize, oldChunkTableOffset) byte-identical.");
  }

  [Test, Category("HappyPath")]
  public void Remove_ReaderHidesTombstonedEntry_FromLiveView() {
    var (image, _) = BuildImage([
      Encoding.UTF8.GetBytes("alpha"),
      Encoding.UTF8.GetBytes("beta"),
      Encoding.UTF8.GetBytes("gamma"),
    ], compressChunks: false);

    using var stream = OpenRw(image);
    ParagonInPlaceModifier.RemoveChunk(stream, "chunk_000001.bin");

    stream.Position = 0;
    using var r = new ParagonReader(stream);
    var liveChunks = r.Entries.Where(e => e.Name.StartsWith("chunk_")).Select(e => e.Name).ToList();
    Assert.That(liveChunks, Is.EquivalentTo(new[] { "chunk_000000.bin", "chunk_000002.bin" }),
      "Tombstoned chunk must NOT appear in the reader's live-entry view.");
  }

  [Test, Category("HappyPath")]
  public void Remove_TombstoneFlagVisibleOnWire_ForChosenSentinel() {
    var (image, oldTableOffset) = BuildImage([
      Encoding.UTF8.GetBytes("alpha"),
      Encoding.UTF8.GetBytes("beta"),
    ], compressChunks: false);

    using var stream = OpenRw(image);
    ParagonInPlaceModifier.RemoveChunk(stream, "chunk_000000.bin");

    var mutated = stream.ToArray();
    // Tombstones append at the OLD chunk-table offset (no body bytes).
    // The new table sits at oldTableOffset. The tombstone is the LAST
    // entry of the new table (index ChunkCount-1).
    var newChunkCount = BinaryPrimitives.ReadUInt32LittleEndian(mutated.AsSpan(ParagonWriter.OffsetChunkCount, 4));
    var newTableOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(mutated.AsSpan(ParagonWriter.OffsetChunkTableOffset, 8));
    Assert.That(newTableOffset, Is.EqualTo(oldTableOffset),
      "Tombstone has no body bytes — new table sits exactly at old table offset.");
    var lastEntryOffset = (int)(newTableOffset + ((long)newChunkCount - 1) * ParagonWriter.ChunkEntrySize);
    Assert.That(mutated[lastEntryOffset + 16], Is.EqualTo(ParagonWriter.TombstoneFlag),
      "Tombstone's IsCompressed byte (+16 of entry) must encode the documented 0xFF sentinel.");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(mutated.AsSpan(lastEntryOffset + 12, 4)),
      Is.EqualTo(0u), "Tombstone's ChunkSize must be 0.");
  }

  [Test, Category("HappyPath")]
  public void Remove_AdvertisesSentinelInMetadata() {
    var (image, _) = BuildImage([
      Encoding.UTF8.GetBytes("alpha"),
      Encoding.UTF8.GetBytes("beta"),
    ]);
    using var stream = OpenRw(image);
    ParagonInPlaceModifier.RemoveChunk(stream, "chunk_000001.bin");

    stream.Position = 0;
    using var r = new ParagonReader(stream);
    var meta = r.Entries.First(e => e.Name == "metadata.ini");
    var text = Encoding.UTF8.GetString(meta.Data);
    Assert.That(text, Does.Contain("cwbp_tombstone_flag=0xFF"),
      "Metadata must document the tombstone sentinel so external tools can interpret the wire.");
    Assert.That(text, Does.Contain("cwbp_rw_scope="),
      "Metadata must describe the R/W scope (Add / Replace / Remove via chunk-table append).");
  }

  [Test, Category("Sad")]
  public void Remove_UnknownChunk_ThrowsFileNotFound() {
    var (image, _) = BuildImage([Encoding.UTF8.GetBytes("only")]);
    using var stream = OpenRw(image);
    Assert.Throws<FileNotFoundException>(() =>
      ParagonInPlaceModifier.RemoveChunk(stream, "chunk_000009.bin"));
  }

  // ── Adler-32 preservation, mixed operations ─────────────────────────

  [Test, Category("HappyPath")]
  public void Adler32_StillValidates_OnUntouchedChunks_AfterMixedOps() {
    // Build 4 chunks, then Add + Replace + Remove and confirm every
    // surviving live chunk's Adler-32 still passes by successfully
    // reading them back.
    var c0 = new byte[64]; new Random(1).NextBytes(c0);
    var c1 = new byte[64]; new Random(2).NextBytes(c1);
    var c2 = new byte[64]; new Random(3).NextBytes(c2);
    var c3 = new byte[64]; new Random(4).NextBytes(c3);
    var (image, _) = BuildImage([c0, c1, c2, c3], compressChunks: true);

    using var stream = OpenRw(image);
    // Mutate: replace c1, remove c2, add a new chunk.
    ParagonInPlaceModifier.ReplaceChunk(stream, "chunk_000001.bin",
      Encoding.UTF8.GetBytes("c1-REPLACED"), compressChunks: true);
    ParagonInPlaceModifier.RemoveChunk(stream, "chunk_000002.bin");
    ParagonInPlaceModifier.AddChunks(stream, [
      ArchiveInputInfo.InMemory("c4.bin", Encoding.UTF8.GetBytes("c4-added")),
    ], compressChunks: true);

    stream.Position = 0;
    Assert.DoesNotThrow(() => {
      using var r = new ParagonReader(stream);
      // Reader throws on Adler-32 mismatch — getting here means every
      // live chunk's checksum is still valid.
      var live = r.Entries.Where(e => e.Name.StartsWith("chunk_")).ToList();
      Assert.That(live.Select(e => e.Name).ToList(), Is.EquivalentTo(new[] {
        "chunk_000000.bin", "chunk_000001.bin", "chunk_000003.bin", "chunk_000004.bin",
      }));
      Assert.That(live.First(e => e.Name == "chunk_000000.bin").Data, Is.EqualTo(c0),
        "c0 is untouched; bytes + Adler-32 still match the original.");
      Assert.That(live.First(e => e.Name == "chunk_000003.bin").Data, Is.EqualTo(c3),
        "c3 is untouched; bytes + Adler-32 still match the original.");
      Assert.That(live.First(e => e.Name == "chunk_000001.bin").Data,
        Is.EqualTo(Encoding.UTF8.GetBytes("c1-REPLACED")));
      Assert.That(live.First(e => e.Name == "chunk_000004.bin").Data,
        Is.EqualTo(Encoding.UTF8.GetBytes("c4-added")));
    });
  }

  // ── Descriptor surface ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AddRoute_DelegatesToInPlaceModifier() {
    var d = new ParagonFormatDescriptor();
    using var output = new MemoryStream();
    d.Create(output, [
      ArchiveInputInfo.InMemory("alpha.bin", Encoding.UTF8.GetBytes("alpha")),
    ], new FormatCreateOptions { MethodName = "stored" });

    // Reuse the same MemoryStream for the in-place append.
    output.Position = 0;
    var preMutationBytes = output.ToArray();
    var oldTableOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(preMutationBytes.AsSpan(ParagonWriter.OffsetChunkTableOffset, 8));

    d.Add(output, [
      ArchiveInputInfo.InMemory("beta.bin", Encoding.UTF8.GetBytes("beta-added")),
    ]);

    var postMutationBytes = output.ToArray();
    var bodyRegionLen = (int)oldTableOffset - ParagonWriter.HeaderSize;
    Assert.That(postMutationBytes.AsSpan(ParagonWriter.HeaderSize, bodyRegionLen).ToArray(),
      Is.EqualTo(preMutationBytes.AsSpan(ParagonWriter.HeaderSize, bodyRegionLen).ToArray()),
      "Descriptor.Add must preserve the pre-mutation chunk-body region byte-identical.");

    output.Position = 0;
    using var r = new ParagonReader(output);
    Assert.That(r.Entries.Where(e => e.Name.StartsWith("chunk_")).Select(e => e.Name).ToList(),
      Is.EquivalentTo(new[] { "chunk_000000.bin", "chunk_000001.bin" }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_RemoveRoute_AppendsTombstoneAndHidesEntry() {
    var d = new ParagonFormatDescriptor();
    using var output = new MemoryStream();
    d.Create(output, [
      ArchiveInputInfo.InMemory("alpha.bin", Encoding.UTF8.GetBytes("alpha")),
      ArchiveInputInfo.InMemory("beta.bin", Encoding.UTF8.GetBytes("beta")),
    ], new FormatCreateOptions { MethodName = "stored" });

    output.Position = 0;
    d.Remove(output, new[] { "chunk_000000.bin" });

    output.Position = 0;
    using var r = new ParagonReader(output);
    var live = r.Entries.Where(e => e.Name.StartsWith("chunk_")).Select(e => e.Name).ToList();
    Assert.That(live, Is.EquivalentTo(new[] { "chunk_000001.bin" }),
      "After Descriptor.Remove the tombstoned entry must disappear from the live view.");
  }

  // ── Vendor-style images stay out of scope ────────────────────────────

  [Test, Category("Sad")]
  public void InPlace_OnVendorStyleImage_ThrowsInvalidOperationException() {
    // Build a minimal PImg image WITHOUT the CWBP discriminator — i.e.
    // looks vendor-style. The modifier must refuse rather than corrupt
    // the image with a chunk-table append at an unknown offset.
    var img = new byte[256];
    Encoding.ASCII.GetBytes("PImg").CopyTo(img.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(4, 2), 0x0002);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(6, 2), 0x0003);
    // Discriminator at +0xF8 is zero — not CWBP.

    using var stream = OpenRw(img);
    Assert.Throws<InvalidOperationException>(() =>
      ParagonInPlaceModifier.AddChunks(stream, [
        ArchiveInputInfo.InMemory("x.bin", new byte[] { 1, 2 }),
      ]));
  }

  // ── Equivalence-class + boundary cases ──────────────────────────────

  [Test, Category("Boundary")]
  public void Add_EmptyPayloadChunk_RoundTrips() {
    var (image, _) = BuildImage([Encoding.UTF8.GetBytes("first")]);
    using var stream = OpenRw(image);
    ParagonInPlaceModifier.AddChunks(stream, [
      ArchiveInputInfo.InMemory("empty.bin", Array.Empty<byte>()),
    ]);

    stream.Position = 0;
    using var r = new ParagonReader(stream);
    var empty = r.Entries.First(e => e.Name == "chunk_000001.bin");
    Assert.That(empty.Data, Has.Length.EqualTo(0));
  }

  [Test, Category("Sad")]
  public void Replace_NonChunkName_Throws() {
    var (image, _) = BuildImage([Encoding.UTF8.GetBytes("alpha")]);
    using var stream = OpenRw(image);
    Assert.Throws<InvalidOperationException>(() =>
      ParagonInPlaceModifier.ReplaceChunk(stream, "metadata.ini", new byte[] { 1 }));
  }

  [Test, Category("Sad")]
  public void InPlace_OnReadOnlyStream_Throws() {
    var (image, _) = BuildImage([Encoding.UTF8.GetBytes("alpha")]);
    using var ro = new MemoryStream(image, writable: false);
    Assert.Throws<ArgumentException>(() =>
      ParagonInPlaceModifier.AddChunks(ro, [
        ArchiveInputInfo.InMemory("x.bin", new byte[] { 1 }),
      ]));
  }

  // ── Determinism ─────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_IsDeterministic_TwiceProducesSameImage() {
    var (baseImage, _) = BuildImage([Encoding.UTF8.GetBytes("alpha")], compressChunks: false);

    using var s1 = OpenRw(baseImage);
    ParagonInPlaceModifier.AddChunks(s1, [
      ArchiveInputInfo.InMemory("b.bin", Encoding.UTF8.GetBytes("beta")),
    ], compressChunks: false);
    var first = s1.ToArray();

    using var s2 = OpenRw(baseImage);
    ParagonInPlaceModifier.AddChunks(s2, [
      ArchiveInputInfo.InMemory("b.bin", Encoding.UTF8.GetBytes("beta")),
    ], compressChunks: false);
    var second = s2.ToArray();

    Assert.That(first, Is.EqualTo(second),
      "Two identical in-place Adds must produce byte-identical images.");
  }
}
