using System.Security.Cryptography;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Sfar;

[TestFixture]
public class SfarTests {

  // ---------------------------------------------------------------------------
  // Synthesizer: emits a minimal but spec-correct SFAR layout we can feed back
  // through SfarReader. Avoids depending on any production writer.
  // ---------------------------------------------------------------------------
  private sealed record SynthEntry(byte[] PathHash, byte[] Payload);

  private static byte[] BuildStoredSfar(IReadOnlyList<SynthEntry> entries, int maxBlockSize = 0x10000, bool tagAsLzx = false) {
    // Layout we emit: header(32) | entry table | block table | data blocks
    // Each entry's data is partitioned into ceil(size/maxBlockSize) stored blocks.

    var blockSlotsPerEntry = entries.Select(e => (e.Payload.Length + maxBlockSize - 1) / maxBlockSize).ToArray();
    var totalBlocks = blockSlotsPerEntry.Sum();

    const int headerSize = 32;
    var entryTableSize = entries.Count * 30;
    var blockTableSize = totalBlocks * 2;

    var entriesOffset = headerSize;
    var blockTableOffset = entriesOffset + entryTableSize;
    var dataOffset = blockTableOffset + blockTableSize;

    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);

    // Header
    bw.Write((uint)0x52414653); // "SFAR" little-endian
    bw.Write((uint)0x00010000); // version
    bw.Write((uint)dataOffset);
    bw.Write((uint)entriesOffset);
    bw.Write((uint)entries.Count);
    bw.Write((uint)blockTableOffset);
    bw.Write((uint)maxBlockSize);
    bw.Write(tagAsLzx ? new byte[] { 0x6C, 0x7A, 0x78, 0x00 } : new byte[] { 0, 0, 0, 0 });

    // Compute per-entry data offsets and block table indices up front
    var dataOffsets = new long[entries.Count];
    var blockIndices = new int[entries.Count];
    var blockSizes = new ushort[totalBlocks];

    var cursorOffset = (long)dataOffset;
    var cursorBlock = 0;
    for (var i = 0; i < entries.Count; ++i) {
      dataOffsets[i] = cursorOffset;
      blockIndices[i] = cursorBlock;
      var p = entries[i].Payload;
      var slots = blockSlotsPerEntry[i];
      var written = 0;
      for (var s = 0; s < slots; ++s) {
        var chunk = Math.Min(maxBlockSize, p.Length - written);
        // Stored-block sentinel: block_size of 0 means "stored, full MaxBlockSize"
        // (the spec's "block_size == MaxBlockSize" wraps to 0 in UInt16 for 64 KiB).
        blockSizes[cursorBlock + s] = 0;
        written += chunk;
        cursorOffset += chunk;
      }
      cursorBlock += slots;
    }

    // Entry table
    for (var i = 0; i < entries.Count; ++i) {
      bw.Write(entries[i].PathHash);             // 16 bytes
      bw.Write(blockIndices[i]);                 // 4-byte LE
      WriteFiveByteLE(bw, entries[i].Payload.Length);
      WriteFiveByteLE(bw, dataOffsets[i]);
    }

    // Block table
    foreach (var sz in blockSizes)
      bw.Write(sz);

    // Data blocks
    foreach (var e in entries)
      bw.Write(e.Payload);

    return ms.ToArray();
  }

  private static void WriteFiveByteLE(BinaryWriter bw, long value) {
    bw.Write((byte)(value & 0xFF));
    bw.Write((byte)((value >> 8) & 0xFF));
    bw.Write((byte)((value >> 16) & 0xFF));
    bw.Write((byte)((value >> 24) & 0xFF));
    bw.Write((byte)((value >> 32) & 0xFF));
  }

  private static byte[] HashPath(string path) {
    var lower = path.ToLowerInvariant().Replace('\\', '/');
    return MD5.HashData(Encoding.UTF8.GetBytes(lower));
  }

  // ---------------------------------------------------------------------------
  // Tests
  // ---------------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Reader_ParsesSyntheticHeader() {
    var payload = "Mass Effect 3 DLC payload"u8.ToArray();
    var bytes = BuildStoredSfar([new SynthEntry(HashPath("dlc/file.bin"), payload)]);

    using var ms = new MemoryStream(bytes);
    using var r = new FileFormat.Sfar.SfarReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.IsLzxCompressed, Is.False);
    Assert.That(r.Entries[0].Size, Is.EqualTo(payload.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesSyntheticMultipleStored() {
    var p1 = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
    var p2 = Enumerable.Range(0, 200).Select(i => (byte)(i ^ 0xAA)).ToArray();
    var p3 = Enumerable.Repeat((byte)0xCC, 1024).ToArray();
    var bytes = BuildStoredSfar([
      new SynthEntry(HashPath("a.bin"), p1),
      new SynthEntry(HashPath("dlc/b.bin"), p2),
      new SynthEntry(HashPath("path/to/c.bin"), p3),
    ]);

    using var ms = new MemoryStream(bytes);
    using var r = new FileFormat.Sfar.SfarReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(p1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(p2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(p3));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[64];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Sfar.SfarReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Reader_NoFilenamesTxt_UsesMd5Names() {
    // Entry 0 is NOT a Filenames.txt manifest (it's binary garbage with a NUL).
    var entry0 = new byte[] { 0xDE, 0xAD, 0x00, 0xBE, 0xEF };
    var bytes = BuildStoredSfar([
      new SynthEntry(HashPath("zero.bin"), entry0),
      new SynthEntry(HashPath("one.bin"),  "payload1"u8.ToArray()),
    ]);

    using var ms = new MemoryStream(bytes);
    using var r = new FileFormat.Sfar.SfarReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(2));
    foreach (var e in r.Entries) {
      Assert.That(e.Name, Does.EndWith(".bin"));
      Assert.That(e.Name.Length, Is.EqualTo(32 + ".bin".Length));
      Assert.That(e.Name[..32], Is.EqualTo(Convert.ToHexString(e.PathHash)));
    }
  }

  [Test, Category("HappyPath")]
  public void Reader_WithFilenamesTxt_UsesRealNames() {
    // Two payloads → manifest must contain exactly two names.
    var manifest = "path/one.txt\npath/two.txt\n"u8.ToArray();
    var p1 = "ONE"u8.ToArray();
    var p2 = "TWO"u8.ToArray();
    var bytes = BuildStoredSfar([
      new SynthEntry(HashPath("Filenames.txt"), manifest),
      new SynthEntry(HashPath("path/one.txt"), p1),
      new SynthEntry(HashPath("path/two.txt"), p2),
    ]);

    using var ms = new MemoryStream(bytes);
    using var r = new FileFormat.Sfar.SfarReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[1].Name, Is.EqualTo("path/one.txt"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("path/two.txt"));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(p1));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(p2));
  }

  [Test, Category("HappyPath")]
  public void Reader_FiveByteIntegers_ParseCorrectly() {
    // We can't actually allocate a 4 GiB file, but we can hand-craft a header where the
    // declared size hits the high byte of the 5-byte LE field. The reader must parse it
    // correctly even though we don't allocate or extract the payload.
    const long bigSize = 0x100000000L; // 4 294 967 296 — value lives in byte index 4

    var hash = new byte[16];
    Array.Fill(hash, (byte)0x42);

    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);
    const int headerSize = 32;
    const int entryTableSize = 30;
    var entriesOffset = headerSize;
    var blockTableOffset = entriesOffset + entryTableSize;
    var dataOffset = blockTableOffset; // no real data (we don't extract)

    bw.Write((uint)0x52414653);
    bw.Write((uint)0x00010000);
    bw.Write((uint)dataOffset);
    bw.Write((uint)entriesOffset);
    bw.Write((uint)1);
    bw.Write((uint)blockTableOffset);
    bw.Write((uint)0x10000);
    bw.Write(new byte[] { 0, 0, 0, 0 });

    bw.Write(hash);
    bw.Write(0);  // BlockTableIndex
    WriteFiveByteLE(bw, bigSize);
    WriteFiveByteLE(bw, dataOffset);
    // Block table is empty → reader should not need it just to enumerate sizes.

    using var read = new MemoryStream(ms.ToArray());
    using var r = new FileFormat.Sfar.SfarReader(read);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(bigSize));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_LzxCompressed_ThrowsCleanly() {
    // Hand-build an SFAR that declares LZX compression and contains a single non-stored
    // block (declared size != maxBlockSize). Extract() must throw NotSupportedException.

    var hash = new byte[16];
    Array.Fill(hash, (byte)0x77);

    const int headerSize = 32;
    const int entryTableSize = 30;
    const int blockTableSize = 2;     // one ushort
    const int maxBlockSize = 0x10000;

    var entriesOffset = headerSize;
    var blockTableOffset = entriesOffset + entryTableSize;
    var dataOffset = blockTableOffset + blockTableSize;
    const int payloadOnDisk = 8;       // declared compressed size — distinct from maxBlockSize
    const int payloadUncompressed = 0x4000;

    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);

    bw.Write((uint)0x52414653);
    bw.Write((uint)0x00010000);
    bw.Write((uint)dataOffset);
    bw.Write((uint)entriesOffset);
    bw.Write((uint)1);
    bw.Write((uint)blockTableOffset);
    bw.Write((uint)maxBlockSize);
    bw.Write(new byte[] { 0x6C, 0x7A, 0x78, 0x00 }); // "lzx\0"

    bw.Write(hash);
    bw.Write(0);
    WriteFiveByteLE(bw, payloadUncompressed);
    WriteFiveByteLE(bw, dataOffset);

    bw.Write((ushort)payloadOnDisk);                // declared compressed size
    bw.Write(new byte[payloadOnDisk]);              // bogus LZX bytes — never decoded

    using var read = new MemoryStream(ms.ToArray());
    using var r = new FileFormat.Sfar.SfarReader(read);
    Assert.That(r.IsLzxCompressed, Is.True);
    var ex = Assert.Throws<NotSupportedException>(() => _ = r.Extract(r.Entries[0]));
    Assert.That(ex!.Message, Does.Contain("LZX"));
  }

  [Test, Category("HappyPath")]
  public void Capabilities_DoesNotIncludeCanCreate() {
    var d = new FileFormat.Sfar.SfarFormatDescriptor();
    Assert.That((d.Capabilities & FormatCapabilities.CanCreate), Is.EqualTo((FormatCapabilities)0));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Sfar.SfarFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Sfar"));
    Assert.That(d.DisplayName, Is.EqualTo("BioWare SFAR"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".sfar"));
    Assert.That(d.Extensions, Contains.Item(".sfar"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x53, 0x46, 0x41, 0x52 }));
    Assert.That(d.Methods[0].Name, Is.EqualTo("sfar"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("SFAR"));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.Description, Does.Contain("Mass Effect 3"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReportsMethodAndSize() {
    var payload = new byte[256];
    new Random(42).NextBytes(payload);
    var bytes = BuildStoredSfar([new SynthEntry(HashPath("entry"), payload)]);

    var d = new FileFormat.Sfar.SfarFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var list = d.List(ms, password: null);

    Assert.That(list, Has.Count.EqualTo(1));
    Assert.That(list[0].OriginalSize, Is.EqualTo(payload.Length));
    Assert.That(list[0].Method, Is.EqualTo("Stored"));
  }

}
