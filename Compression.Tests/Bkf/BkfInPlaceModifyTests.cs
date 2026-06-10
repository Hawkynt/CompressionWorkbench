using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Bkf;

namespace Compression.Tests.Bkf;

/// <summary>
/// In-place R/W tests for the BKF (MTF) descriptor. Verifies that
/// <see cref="BkfInPlaceModifier.AddFile"/> preserves existing DBLK bytes at
/// their original offsets, that <see cref="BkfInPlaceModifier.RemoveFile"/>
/// tombstones target FILEs without touching neighbours, and that the
/// descriptor surface routes Add / Remove through the modifier.
/// </summary>
[TestFixture]
public class BkfInPlaceModifyTests {

  private const int FlbSize = 1024;
  private const int CbhSize = 52;
  private const int StreamHdr = 22;

  // ── Synthetic MTF builder (mirrors BkfReaderTests' MtfBuilder) ────────

  private sealed class MtfBuilder {
    private readonly MemoryStream _ms = new();

    public MtfBuilder AddTape() {
      var block = new byte[FlbSize];
      WriteCbh(block, "TAPE", stringType: 1);
      BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(52), FlbSize);
      _ms.Write(block, 0, block.Length);
      return this;
    }
    public MtfBuilder AddSset() => this.AddContainer("SSET");
    public MtfBuilder AddVolb() => this.AddContainer("VOLB");
    public MtfBuilder AddEset() => this.AddContainer("ESET");
    public MtfBuilder AddEotm() {
      var block = new byte[FlbSize];
      WriteCbh(block, "EOTM", stringType: 0);
      _ms.Write(block, 0, block.Length);
      return this;
    }
    public MtfBuilder AddFile(string name, byte[] content) {
      var nameBytes = Encoding.Latin1.GetBytes(name);
      var fnamFootprint = StreamFootprint(nameBytes.Length);
      var stanFootprint = StreamFootprint(content.Length);
      var rawSize = CbhSize + fnamFootprint + stanFootprint;
      var paddedSize = RoundUp(rawSize, FlbSize);
      var block = new byte[paddedSize];
      WriteCbh(block, "FILE", stringType: 1);
      var afterFnam = WriteStream(block, CbhSize, "FNAM", nameBytes);
      WriteStream(block, afterFnam, "STAN", content);
      _ms.Write(block, 0, block.Length);
      return this;
    }
    public byte[] Build() => _ms.ToArray();

    private MtfBuilder AddContainer(string type) {
      var block = new byte[FlbSize];
      WriteCbh(block, type, stringType: 1);
      _ms.Write(block, 0, block.Length);
      return this;
    }
    private static void WriteCbh(byte[] block, string blockType, ushort stringType) {
      Encoding.ASCII.GetBytes(blockType).CopyTo(block, 0);
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), CbhSize);
      block[10] = 14; block[11] = 1;
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(46), stringType);
    }
    private static int WriteStream(byte[] block, int offset, string streamId, byte[] payload) {
      Encoding.ASCII.GetBytes(streamId).CopyTo(block, offset);
      BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(offset + 8), (ulong)payload.Length);
      var dataStart = offset + StreamHdr;
      if (payload.Length > 0) Array.Copy(payload, 0, block, dataStart, payload.Length);
      var end = dataStart + payload.Length;
      return RoundUp(end, 4);
    }
    private static int StreamFootprint(int payloadLength) => RoundUp(StreamHdr + payloadLength, 4);
    private static int RoundUp(int value, int alignment) {
      var rem = value % alignment;
      return rem == 0 ? value : value + (alignment - rem);
    }
  }

  private static MemoryStream BuildStream(Func<MtfBuilder, MtfBuilder> build) {
    var ms = new MemoryStream();
    var bytes = build(new MtfBuilder()).Build();
    ms.Write(bytes);
    ms.Position = 0;
    return ms;
  }

  // ── Descriptor surface ───────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify() {
    var d = new BkfFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  // ── Add ──────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Add_NewEntry_AppearsInListing() {
    using var ms = BuildStream(b => b.AddTape().AddSset().AddVolb()
      .AddFile("existing.txt", "old"u8.ToArray()).AddEset().AddEotm());

    BkfInPlaceModifier.AddFile(ms, "fresh.bin", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

    ms.Position = 0;
    var r = new BkfReader(ms);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("existing.txt"));
    Assert.That(names, Does.Contain("fresh.bin"));
    var added = r.Entries.First(e => e.Name == "fresh.bin");
    Assert.That(r.Extract(added), Is.EqualTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
  }

  [Test, Category("RoundTrip")]
  public void Add_PreservesAllExistingDblkBytes() {
    var before = new MtfBuilder().AddTape().AddSset().AddVolb()
      .AddFile("alpha.txt", "alpha-payload"u8.ToArray())
      .AddFile("bravo.bin", "bravo-bytes-here"u8.ToArray())
      .AddEset().AddEotm().Build();

    // The original layout: 4×FLB containers + 2×FLB files + 1×FLB EOTM = 7×1024.
    // Compute the cut point: everything before the EOTM block must survive.
    var eotmPos = LocateBlock(before, "EOTM");
    Assert.That(eotmPos, Is.GreaterThan(0));

    using var ms = new MemoryStream();
    ms.Write(before);
    BkfInPlaceModifier.AddFile(ms, "added.bin", new byte[] { 1, 2, 3 });

    var after = ms.ToArray();
    // Every byte from 0..eotmPos must remain byte-identical.
    var headBefore = before.AsSpan(0, eotmPos).ToArray();
    var headAfter = after.AsSpan(0, eotmPos).ToArray();
    Assert.That(headAfter, Is.EqualTo(headBefore),
      "All pre-EOTM bytes must remain byte-identical after Add.");
  }

  [Test, Category("RoundTrip")]
  public void Add_StillTerminatesWithEotm() {
    using var ms = BuildStream(b => b.AddTape().AddSset().AddVolb().AddEset().AddEotm());
    BkfInPlaceModifier.AddFile(ms, "new.txt", "payload"u8.ToArray());

    var after = ms.ToArray();
    var eotmPos = LocateBlock(after, "EOTM");
    Assert.That(eotmPos, Is.GreaterThan(0), "EOTM must still exist after Add.");
    // EOTM must be the last DBLK.
    var remaining = after.Length - (eotmPos + FlbSize);
    Assert.That(remaining, Is.EqualTo(0), "Nothing must follow the re-emitted EOTM.");
  }

  [Test, Category("RoundTrip")]
  public void Add_WithoutEotm_AppendsAtEof() {
    // No EOTM in input — modifier falls back to EOF append.
    using var ms = BuildStream(b => b.AddTape().AddSset().AddVolb()
      .AddFile("only.txt", "x"u8.ToArray()).AddEset());

    var sizeBefore = ms.Length;
    BkfInPlaceModifier.AddFile(ms, "appended.bin", new byte[] { 9, 9, 9, 9 });

    ms.Position = 0;
    var r = new BkfReader(ms);
    Assert.That(r.Entries.Any(e => e.Name == "appended.bin"), Is.True);
    Assert.That(ms.Length, Is.GreaterThan(sizeBefore));
  }

  [Test, Category("EdgeCase")]
  public void Add_EmptyPayload_RoundTrips() {
    using var ms = BuildStream(b => b.AddTape().AddSset().AddVolb()
      .AddFile("x.txt", "x"u8.ToArray()).AddEset().AddEotm());

    BkfInPlaceModifier.AddFile(ms, "empty.bin", System.Array.Empty<byte>());

    ms.Position = 0;
    var r = new BkfReader(ms);
    var empty = r.Entries.FirstOrDefault(e => e.Name == "empty.bin");
    Assert.That(empty, Is.Not.Null);
    Assert.That(empty!.Size, Is.EqualTo(0));
    Assert.That(r.Extract(empty), Is.Empty);
  }

  // ── Remove ───────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Remove_NamedEntry_DropsFromListing() {
    using var ms = BuildStream(b => b.AddTape().AddSset().AddVolb()
      .AddFile("keep.txt", "alive"u8.ToArray())
      .AddFile("drop.txt", "doomed"u8.ToArray())
      .AddEset().AddEotm());

    var removed = BkfInPlaceModifier.RemoveFile(ms, "drop.txt");
    Assert.That(removed, Is.True);

    ms.Position = 0;
    var r = new BkfReader(ms);
    var names = r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("keep.txt"));
    Assert.That(names, Does.Not.Contain("drop.txt"));
  }

  [Test, Category("RoundTrip")]
  public void Remove_PreservesNeighbourDblkBytes() {
    var raw = new MtfBuilder().AddTape().AddSset().AddVolb()
      .AddFile("alpha.txt", "alpha-payload"u8.ToArray())
      .AddFile("bravo.bin", new byte[] { 1, 2, 3, 4, 5 })
      .AddFile("gamma.txt", "gamma data"u8.ToArray())
      .AddEset().AddEotm().Build();

    var alphaPos = LocateBlock(raw, "FILE");
    var bravoPos = LocateBlock(raw, "FILE", startAt: alphaPos + 1);
    var gammaPos = LocateBlock(raw, "FILE", startAt: bravoPos + 1);
    Assert.That(alphaPos, Is.GreaterThan(0));
    Assert.That(bravoPos, Is.GreaterThan(alphaPos));
    Assert.That(gammaPos, Is.GreaterThan(bravoPos));

    var alphaBefore = raw.AsSpan(alphaPos, FlbSize).ToArray();
    var gammaBefore = raw.AsSpan(gammaPos, FlbSize).ToArray();

    using var ms = new MemoryStream();
    ms.Write(raw);
    BkfInPlaceModifier.RemoveFile(ms, "bravo.bin");

    var after = ms.ToArray();
    var alphaAfter = after.AsSpan(alphaPos, FlbSize).ToArray();
    var gammaAfter = after.AsSpan(gammaPos, FlbSize).ToArray();

    Assert.That(alphaAfter, Is.EqualTo(alphaBefore),
      "alpha FILE DBLK must be byte-identical after removing bravo.");
    Assert.That(gammaAfter, Is.EqualTo(gammaBefore),
      "gamma FILE DBLK must be byte-identical after removing bravo.");
  }

  [Test, Category("Security")]
  public void Remove_ZeroWipesPayloadBytes() {
    var secret = "REDACTED-SECRET-SECRET-SECRET"u8.ToArray();
    using var ms = BuildStream(b => b.AddTape().AddSset().AddVolb()
      .AddFile("keep.txt", "alive"u8.ToArray())
      .AddFile("secret.bin", secret)
      .AddEset().AddEotm());

    BkfInPlaceModifier.RemoveFile(ms, "secret.bin");

    var after = ms.ToArray();
    var matched = false;
    for (var i = 0; i + secret.Length <= after.Length; i++) {
      if (after.AsSpan(i, secret.Length).SequenceEqual(secret)) { matched = true; break; }
    }
    Assert.That(matched, Is.False, "Removed payload bytes must not survive anywhere in the stream.");
  }

  [Test, Category("RoundTrip")]
  public void Remove_TombstoneTypeIsXXXX() {
    var raw = new MtfBuilder().AddTape().AddSset().AddVolb()
      .AddFile("target.txt", "doomed"u8.ToArray())
      .AddEset().AddEotm().Build();

    var filePos = LocateBlock(raw, "FILE");
    using var ms = new MemoryStream();
    ms.Write(raw);
    BkfInPlaceModifier.RemoveFile(ms, "target.txt");
    var after = ms.ToArray();

    var tomb = Encoding.ASCII.GetString(after, filePos, 4);
    Assert.That(tomb, Is.EqualTo("XXXX"), "Tombstoned DBLK must carry the XXXX sentinel.");
  }

  [Test, Category("EdgeCase")]
  public void Remove_NonexistentName_ReturnsFalse() {
    using var ms = BuildStream(b => b.AddTape().AddSset().AddVolb()
      .AddFile("only.txt", "x"u8.ToArray()).AddEset().AddEotm());

    var result = BkfInPlaceModifier.RemoveFile(ms, "does-not-exist.bin");
    Assert.That(result, Is.False);
  }

  // ── Mutate then extract ─────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_RoundTrips() {
    using var ms = BuildStream(b => b.AddTape().AddSset().AddVolb()
      .AddFile("keep.txt", "alive"u8.ToArray())
      .AddFile("drop.txt", "doomed"u8.ToArray())
      .AddEset().AddEotm());

    BkfInPlaceModifier.RemoveFile(ms, "drop.txt");
    BkfInPlaceModifier.AddFile(ms, "fresh.bin", "minted"u8.ToArray());

    ms.Position = 0;
    var r = new BkfReader(ms);
    var keep = r.Entries.First(e => e.Name == "keep.txt");
    var fresh = r.Entries.First(e => e.Name == "fresh.bin");
    Assert.That(r.Extract(keep), Is.EqualTo("alive"u8.ToArray()));
    Assert.That(r.Extract(fresh), Is.EqualTo("minted"u8.ToArray()));
    Assert.That(r.Entries.Any(e => e.Name == "drop.txt"), Is.False);
  }

  // ── Descriptor surface routing ──────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Descriptor_Add_RoutesThroughModifier() {
    var d = new BkfFormatDescriptor();
    using var ms = BuildStream(b => b.AddTape().AddSset().AddVolb()
      .AddFile("old.txt", "old"u8.ToArray()).AddEset().AddEotm());

    var input = ArchiveInputInfo.InMemory("added.bin", new byte[] { 0xAA, 0xBB });
    ((IArchiveModifiable)d).Add(ms, new[] { input });

    ms.Position = 0;
    var listed = d.List(ms, null).Select(e => e.Name).ToList();
    Assert.That(listed, Does.Contain("added.bin"));
    Assert.That(listed, Does.Contain("old.txt"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Remove_RoutesThroughModifier() {
    var d = new BkfFormatDescriptor();
    using var ms = BuildStream(b => b.AddTape().AddSset().AddVolb()
      .AddFile("alive.txt", "alive"u8.ToArray())
      .AddFile("doomed.txt", "doomed"u8.ToArray())
      .AddEset().AddEotm());

    ((IArchiveModifiable)d).Remove(ms, new[] { "doomed.txt" });

    ms.Position = 0;
    var listed = d.List(ms, null).Select(e => e.Name).ToList();
    Assert.That(listed, Does.Contain("alive.txt"));
    Assert.That(listed, Does.Not.Contain("doomed.txt"));
  }

  // ── Helpers ─────────────────────────────────────────────────────────

  private static int LocateBlock(byte[] data, string fourCc, int startAt = 0) {
    var marker = Encoding.ASCII.GetBytes(fourCc);
    for (var pos = ((startAt + FlbSize - 1) / FlbSize) * FlbSize; pos + 4 <= data.Length; pos += FlbSize) {
      if (data.AsSpan(pos, 4).SequenceEqual(marker)) return pos;
    }
    return -1;
  }
}
