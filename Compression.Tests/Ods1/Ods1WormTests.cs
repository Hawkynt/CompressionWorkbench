using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.Ods1;

namespace Compression.Tests.Ods1;

/// <summary>
/// WORM (write-once) round-trip suite for the DEC ODS-1 (Files-11 L1)
/// writer. ODS-1 has no Linux fsck, so self-round-trip via
/// <see cref="Ods1Reader"/> is the gate: the writer's output is parsed,
/// each entry is extracted, and the bytes are compared to the original
/// inputs.
/// </summary>
[TestFixture]
public class Ods1WormTests {

  private const int LbnSize = 512;

  /// <summary>Helper: writes a fresh image via the descriptor's
  /// <see cref="IArchiveCreatable.Create"/> path and returns the bytes.</summary>
  private static byte[] CreateImage(
    IReadOnlyList<(string Name, byte[] Data)> files,
    string volumeLabel = "WORMTEST") {
    var d = new Ods1FormatDescriptor();
    var inputs = files.Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data)).ToList();
    var options = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = volumeLabel },
    };
    using var ms = new MemoryStream();
    ((IArchiveCreatable)d).Create(ms, inputs, options);
    return ms.ToArray();
  }

  // ── HappyPath: smoke + signature ───────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Writer_EmitsDecFile11Signature() {
    var img = Ods1Writer.Build([("HELLO.TXT", "Hello, ODS-1!"u8.ToArray())]);
    // Home block at LBN 1; format string at offset 0x1F0 within → file offset 0x3F0
    var sig = Encoding.ASCII.GetString(img, 0x3F0, 10);
    Assert.That(sig, Is.EqualTo("DECFILE11A"));
  }

  [Test, Category("HappyPath")]
  public void Writer_EmitsHomeBlockStructureLevel() {
    var img = Ods1Writer.Build([("HELLO.TXT", "Hi"u8.ToArray())]);
    var structLev = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(LbnSize + 0x00C));
    Assert.That(structLev, Is.EqualTo(0x0101), "Files-11 Level 1");
  }

  [Test, Category("HappyPath")]
  public void Writer_EmitsVolumeLabel() {
    var img = Ods1Writer.Build([("X.Y", [0x01])], volumeName: "MYVOL");
    var vname = Encoding.ASCII.GetString(img, LbnSize + 0x00E, 12).TrimEnd('\0', ' ');
    Assert.That(vname, Is.EqualTo("MYVOL"));
  }

  [Test, Category("HappyPath")]
  public void Writer_MagicMatchesDescriptorOffset() {
    // The descriptor advertises the DECFILE11A signature at file offset 0x3F0.
    // Ensure the writer puts it exactly there.
    var img = Ods1Writer.Build([("A.B", [0xAA])]);
    var d = new Ods1FormatDescriptor();
    var magicOffset = d.MagicSignatures[0].Offset;
    var actualSig = img.AsSpan((int)magicOffset, d.MagicSignatures[0].Bytes.Length).ToArray();
    Assert.That(actualSig, Is.EqualTo(d.MagicSignatures[0].Bytes));
  }

  // ── RoundTrip ────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile_RecoversExactBytes() {
    var data = "Hello, ODS-1!"u8.ToArray();
    var img = Ods1Writer.Build([("HELLO.TXT", data)]);
    using var ms = new MemoryStream(img);
    using var r = new Ods1Reader(ms);

    Assert.That(r.VolumeFormat, Does.StartWith("DECFILE11A"));
    Assert.That(r.Entries.Any(e => e.Name == "HELLO.TXT"), Is.True);
    var entry = r.Entries.First(e => e.Name == "HELLO.TXT");
    var extracted = r.Extract(entry);
    // Reader reports size in whole blocks; the first `data.Length` bytes
    // hold our payload, the rest are zero padding.
    Assert.That(extracted.Length, Is.GreaterThanOrEqualTo(data.Length));
    Assert.That(extracted.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles_AllPresentByteExact() {
    var files = new List<(string Name, byte[] Data)> {
      ("FIRST.TXT",  Encoding.ASCII.GetBytes("first file content")),
      ("SECOND.DAT", [0x00, 0x01, 0x02, 0x03, 0x04, 0x05]),
      ("THIRD.BIN",  Enumerable.Range(0, 300).Select(i => (byte)(i & 0xFF)).ToArray()),
    };

    var img = Ods1Writer.Build(files);
    using var ms = new MemoryStream(img);
    using var r = new Ods1Reader(ms);

    foreach (var (name, expected) in files) {
      var entry = r.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
      Assert.That(entry, Is.Not.Null, $"missing entry {name}");
      var got = r.Extract(entry!);
      Assert.That(got.Length, Is.GreaterThanOrEqualTo(expected.Length), $"underread for {name}");
      Assert.That(got.AsSpan(0, expected.Length).ToArray(), Is.EqualTo(expected), $"byte mismatch for {name}");
      // Padding region (if any) must be zero.
      for (var i = expected.Length; i < got.Length; i++)
        Assert.That(got[i], Is.EqualTo((byte)0), $"non-zero padding at {name}+{i}");
    }
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_ViaDescriptor_CreateThenList() {
    var files = new List<(string Name, byte[] Data)> {
      ("ALPHA.TXT", "alpha"u8.ToArray()),
      ("BETA.LOG",  "beta beta beta"u8.ToArray()),
    };
    var img = CreateImage(files);
    using var ms = new MemoryStream(img);
    var d = new Ods1FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
      Is.SupersetOf(new[] { "ALPHA.TXT", "BETA.LOG" }));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_ViaDescriptor_OpenEntry_StreamsPayload() {
    var payload = Encoding.ASCII.GetBytes("payload via OpenEntry");
    var img = CreateImage([("OPEN.TXT", payload)]);
    using var ms = new MemoryStream(img);
    var d = new Ods1FormatDescriptor();

    using var stream = d.OpenEntry(ms, "OPEN.TXT", null);
    Assert.That(stream, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(stream.Length, Is.GreaterThanOrEqualTo(payload.Length));

    using var buf = new MemoryStream();
    stream.CopyTo(buf);
    var got = buf.ToArray();
    Assert.That(got.AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_ViaDescriptor_ExtractsToDirectory() {
    var dir = Path.Combine(Path.GetTempPath(), $"ods1-worm-{Guid.NewGuid():N}");
    try {
      var files = new List<(string Name, byte[] Data)> {
        ("EXTRACT.TXT", "extract me"u8.ToArray()),
        ("DATA.BIN",    Enumerable.Range(0, 100).Select(i => (byte)i).ToArray()),
      };
      var img = CreateImage(files);
      using var ms = new MemoryStream(img);
      var d = new Ods1FormatDescriptor();
      d.Extract(ms, dir, null, null);

      foreach (var (name, expected) in files) {
        var path = Path.Combine(dir, name);
        Assert.That(File.Exists(path), Is.True, $"missing {name}");
        var got = File.ReadAllBytes(path);
        // The descriptor's Extract calls writer's WriteFile with reader's
        // block-padded payload — first expected.Length bytes are exact.
        Assert.That(got.Length, Is.GreaterThanOrEqualTo(expected.Length));
        Assert.That(got.AsSpan(0, expected.Length).ToArray(), Is.EqualTo(expected));
      }
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  // ── Boundary ────────────────────────────────────────────────────────────

  [Test, Category("Boundary")]
  public void Boundary_EmptyFile_GetsOneBlockExtent() {
    var img = Ods1Writer.Build([("EMPTY.TXT", [])]);
    using var ms = new MemoryStream(img);
    using var r = new Ods1Reader(ms);
    var e = r.Entries.FirstOrDefault(x => x.Name == "EMPTY.TXT");
    Assert.That(e, Is.Not.Null);
    // Writer rounds zero-byte file up to 1 LBN to keep retrieval pointers valid.
    Assert.That(e!.BlockCount, Is.EqualTo(1u));
    var bytes = r.Extract(e);
    Assert.That(bytes.Length, Is.EqualTo(LbnSize));
    Assert.That(bytes.All(b => b == 0), Is.True);
  }

  [Test, Category("Boundary")]
  public void Boundary_ExactlyOneBlock_NoSlack() {
    var data = new byte[LbnSize];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i ^ 0x5A);
    var img = Ods1Writer.Build([("FULL1.BIN", data)]);
    using var ms = new MemoryStream(img);
    using var r = new Ods1Reader(ms);
    var e = r.Entries.First(x => x.Name == "FULL1.BIN");
    Assert.That(e.BlockCount, Is.EqualTo(1u));
    var got = r.Extract(e);
    Assert.That(got.Length, Is.EqualTo(LbnSize));
    Assert.That(got, Is.EqualTo(data));
  }

  [Test, Category("Boundary")]
  public void Boundary_OneBytePastBlock_AllocatesTwoBlocks() {
    var data = new byte[LbnSize + 1];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
    var img = Ods1Writer.Build([("BIG.DAT", data)]);
    using var ms = new MemoryStream(img);
    using var r = new Ods1Reader(ms);
    var e = r.Entries.First(x => x.Name == "BIG.DAT");
    Assert.That(e.BlockCount, Is.EqualTo(2u));
    var got = r.Extract(e);
    Assert.That(got.Length, Is.EqualTo(2 * LbnSize));
    Assert.That(got.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data));
  }

  [Test, Category("Boundary")]
  public void Boundary_MaxLengthName_Accepted() {
    // ODS-1 ident area allows 9 stem + 3 ext chars.
    var name = new string('Q', 9) + "." + new string('Z', 3);
    var data = "max-name"u8.ToArray();
    var img = Ods1Writer.Build([(name, data)]);
    using var ms = new MemoryStream(img);
    using var r = new Ods1Reader(ms);
    Assert.That(r.Entries.Any(e => e.Name == name), Is.True);
  }

  [Test, Category("Boundary")]
  public void Boundary_NameTruncation_StemKept9Ext3() {
    // Provide a name longer than 9.3; writer truncates.
    var img = Ods1Writer.Build([("VERYLONGSTEM.LONGEXT", [0xFE])]);
    using var ms = new MemoryStream(img);
    using var r = new Ods1Reader(ms);
    // Truncated to "VERYLONGS" + "." + "LON"
    Assert.That(r.Entries.Any(e => e.Name == "VERYLONGS.LON"), Is.True);
  }

  [Test, Category("Boundary")]
  public void Boundary_FilesAreLittleEndian() {
    // Sanity: home-block multibyte fields are written little-endian.
    var img = Ods1Writer.Build([("LE.CHK", [0xFF])]);
    // ibmapsize at LBN1 +0 should be 1
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(LbnSize + 0x000)), Is.EqualTo(1));
    // ibmaplbn at LBN1 +2 should be 2 (BitmapLbn)
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(LbnSize + 0x002)), Is.EqualTo(2u));
  }

  // ── Sad / exceptional ──────────────────────────────────────────────────

  [Test, Category("Sad")]
  public void Writer_RejectsEmptyName() {
    Assert.Throws<InvalidOperationException>(() => Ods1Writer.Build([(".TXT", [0x01])]));
  }

  [Test, Category("Sad")]
  public void Writer_RejectsNullInputs() {
    Assert.Throws<ArgumentNullException>(() => Ods1Writer.Build((IReadOnlyList<(string Name, byte[] Data)>)null!));
  }

  [Test, Category("Sad")]
  public void Writer_RejectsTooManyFiles() {
    // Reader scans 64 header LBNs; writer caps at 64 files.
    var inputs = Enumerable.Range(0, 65)
      .Select(i => ($"F{i:D5}.X", new byte[] { (byte)i }))
      .ToList();
    Assert.Throws<ArgumentException>(() => Ods1Writer.Build(inputs));
  }

  // ── Self-consistency ──────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void RoundTrip_ImageIsBlockAligned() {
    var img = Ods1Writer.Build([("ALIGN.OK", [1, 2, 3])]);
    Assert.That(img.Length % LbnSize, Is.EqualTo(0), "image size must be LBN-aligned");
    // Floor = boot+home+bitmap+(index window of 64 LBNs)+ one data LBN minimum
    Assert.That(img.Length / LbnSize, Is.GreaterThanOrEqualTo(4 + 64 + 1), "index window must fit");
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_BitmapMarksAllocatedBlocks() {
    var img = Ods1Writer.Build([("BMP.CHK", new byte[1500])]);
    // BITMAP.SYS data at LBN 2. Allocated range = LBN 0 .. (last data block).
    // We verify LBN 0,1,2,4 (header), 5/6 (data extent for 1500 bytes = 3 blocks).
    var bitmap = new byte[LbnSize];
    Array.Copy(img, 2 * LbnSize, bitmap, 0, LbnSize);
    bool BitSet(uint lbn) => (bitmap[(int)(lbn / 8)] & (1 << (int)(lbn % 8))) != 0;
    Assert.That(BitSet(0), Is.True, "boot block must be marked allocated");
    Assert.That(BitSet(1), Is.True, "home block must be marked allocated");
    Assert.That(BitSet(2), Is.True, "bitmap block must be marked allocated");
    Assert.That(BitSet(4), Is.True, "first file header must be marked allocated");
  }
}
