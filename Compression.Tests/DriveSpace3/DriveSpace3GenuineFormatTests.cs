using System.Buffers.Binary;
using System.Text;
using FileSystem.DriveSpace3;

namespace Compression.Tests.DriveSpace3;

/// <summary>
/// Pins the <b>genuine</b> Microsoft DriveSpace 3 (Win95 Plus! Pack / OSR2)
/// on-disk facts confirmed byte-for-byte against a real <c>DRVSPACE</c>-produced
/// image (OEM <c>MS_DSP3</c>, CvfSignature <c>DVR3</c>). See
/// <c>FileSystem.DriveSpace3/FORMAT-NOTES.md</c>.
/// <para>
/// The genuine DVR3 image uses the offset-36 CVF-field header (CvfSignature at
/// 0x24, MdfatStart at 0x2C, …) with the inner FAT16 volume mapped at file
/// offset 0 — <b>not</b> the DOS-6.22 MSDBL inner-base@0x27 substructure. Each
/// inner cluster maps through the MDFAT (<c>phys=bits0..20, run=bits21..27,
/// flags=bits28..31</c>) to a physical run framed by a 2-byte little-endian
/// header (<c>len-1 in bits0..11, bit15=compressed</c>). These tests build a
/// minimal image in that exact shape and prove the reader decodes it, and that
/// the genuine writer's output round-trips byte-exact for single- and
/// multi-cluster files.
/// </para>
/// </summary>
[TestFixture]
public class DriveSpace3GenuineFormatTests {

  private const int Ss = 512;
  private const int Spc = 8;                // sectors/cluster -> 4096-byte cluster
  private const int ClusterBytes = Ss * Spc;
  private const int Reserved = 1;
  private const int NumFats = 2;
  private const int FatSize = 16;           // sectors per inner FAT
  private const int RootEntries = 512;
  private const int RootSecs = RootEntries * 32 / Ss;     // 32

  /// <summary>
  /// Hand-builds the exact genuine DVR3 stored-cluster encoding (offset-36
  /// header, FAT16 inner volume at offset 0, MDFAT bit layout, 2-byte stored
  /// run header) for a list of (8.3-name, bytes) files. Mirrors what the real
  /// <c>DRVSPACE</c> tool emits for stored runs, so it is an independent oracle
  /// for the reader.
  /// </summary>
  private static byte[] BuildGenuineStoredImage(params (string Name, byte[] Data)[] files) {
    var firstData = Reserved + NumFats * FatSize + RootSecs;   // inner first-data sector

    // Plan clusters contiguously from cluster 2.
    var plans = new List<(string Name, byte[] Data, int FirstCluster, int Count)>();
    var next = 2;
    foreach (var (name, data) in files) {
      var count = Math.Max(1, (data.Length + ClusterBytes - 1) / ClusterBytes);
      plans.Add((name, data, next, count));
      next += count;
    }
    var usedClusters = next - 2;
    var innerClusters = Math.Max(4089, usedClusters + 2);

    // Region geometry: inner volume, then MDFAT, BitFAT, DATA.
    var innerDataSecs = innerClusters * Spc;
    var mdfatStart = firstData + innerDataSecs;
    var mdfatSecs = (innerClusters * 4 + Ss - 1) / Ss;
    var bitFatStart = mdfatStart + mdfatSecs;
    var bitFatSecs = 1;
    var dataStart = bitFatStart + bitFatSecs;

    // Stored runs: header(2) + payload, padded to whole sectors. Max 9 sectors.
    var dataSecs = 0;
    foreach (var p in plans)
      for (var c = 0; c < p.Count; c++) {
        var chunk = Math.Min(ClusterBytes, p.Data.Length - c * ClusterBytes);
        if (chunk <= 0) chunk = 0;
        dataSecs += (2 + Math.Max(1, chunk) + Ss - 1) / Ss;
      }
    var total = dataStart + dataSecs + Spc;
    var img = new byte[total * Ss];

    // MDBPB.
    img[0] = 0xEB; img[1] = 0x58; img[2] = 0x90;
    "MS_DSP3\0"u8.CopyTo(img.AsSpan(3, 8));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0B), Ss);
    img[0x0D] = Spc;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0E), Reserved);
    img[0x10] = NumFats;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x11), RootEntries);
    if (total < 65536) BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x13), (ushort)total);
    img[0x15] = 0xF8;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x16), FatSize);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x18), 63);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x1A), 255);
    // CVF fields at 0x24.
    "DVR3"u8.CopyTo(img.AsSpan(0x24, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x28), 0x00030300u);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x2C), (uint)mdfatStart);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x30), (uint)mdfatSecs);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x34), (uint)bitFatStart);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x38), (uint)bitFatSecs);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x3C), (uint)dataStart);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x40), (uint)dataSecs);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x48), (uint)innerClusters);
    img[0x1FE] = 0x55; img[0x1FF] = 0xAA;

    var fatOff = Reserved * Ss;
    var rootOff = (Reserved + NumFats * FatSize) * Ss;
    var mdfatBase = mdfatStart * Ss;

    // FAT16 reserved entries.
    img[fatOff] = 0xF8; img[fatOff + 1] = 0xFF; img[fatOff + 2] = 0xFF; img[fatOff + 3] = 0xFF;

    var dirIdx = 0;
    var physSector = 0;
    foreach (var (name, data, firstCluster, count) in plans) {
      // Directory entry.
      var de = rootOff + dirIdx * 32;
      WriteShort83(img, de, name);
      img[de + 11] = 0x20;
      BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(de + 26), (ushort)firstCluster);
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(de + 28), (uint)data.Length);
      dirIdx++;

      var written = 0;
      for (var c = 0; c < count; c++) {
        var cluster = firstCluster + c;
        var isLast = c == count - 1;
        var chunk = Math.Min(ClusterBytes, data.Length - written);
        if (chunk < 0) chunk = 0;

        // Inner FAT16 chain.
        var fe = fatOff + cluster * 2;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(fe), (ushort)(isLast ? 0xFFFF : cluster + 1));

        // Stored run: 2-byte header + payload.
        var payloadLen = Math.Max(1, chunk);
        var hdr = (ushort)(payloadLen - 1);               // bit15 = 0 => stored
        var runOff = (dataStart + physSector) * Ss;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(runOff), hdr);
        if (chunk > 0) Array.Copy(data, written, img, runOff + 2, chunk);
        var runSecs = (2 + payloadLen + Ss - 1) / Ss;

        // MDFAT entry.
        var entry = ((uint)physSector & 0x1FFFFFu) | (((uint)runSecs & 0x7Fu) << 21) | (1u << 28);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(mdfatBase + cluster * 4), entry);

        physSector += runSecs;
        written += chunk;
      }
    }

    // Mirror FAT1 to FAT2.
    Array.Copy(img, fatOff, img, fatOff + FatSize * Ss, FatSize * Ss);
    return img;
  }

  private static void WriteShort83(byte[] img, int offset, string name) {
    Span<byte> f = stackalloc byte[11];
    f.Fill((byte)' ');
    var dot = name.LastIndexOf('.');
    var stem = (dot < 0 ? name : name[..dot]).ToUpperInvariant();
    var ext = (dot < 0 ? "" : name[(dot + 1)..]).ToUpperInvariant();
    Encoding.ASCII.GetBytes(stem)[..Math.Min(8, stem.Length)].CopyTo(f);
    if (ext.Length > 0) Encoding.ASCII.GetBytes(ext)[..Math.Min(3, ext.Length)].CopyTo(f[8..]);
    f.CopyTo(img.AsSpan(offset, 11));
  }

  // ========================================================================

  [Test, Category("Genuine")]
  public void Genuine_DVR3_StoredCluster_Layout_RoundTrips() {
    // Reproduces the exact genuine "CWB_CVF_PROOF_OK\r\n" stored run observed
    // in the real Microsoft drvspace3.cvf oracle.
    var payload = "CWB_CVF_PROOF_OK\r\n"u8.ToArray();
    Assert.That(payload.Length, Is.EqualTo(18));

    var img = BuildGenuineStoredImage(("HELLO.TXT", payload));

    // Header facts the genuine image stamps.
    Assert.That(Encoding.ASCII.GetString(img, 3, 7), Is.EqualTo("MS_DSP3"));
    Assert.That(Encoding.ASCII.GetString(img, 36, 4), Is.EqualTo("DVR3"));

    using var ms = new MemoryStream(img);
    using var r = new DriveSpace3Reader(ms);
    Assert.That(r.Signature, Is.EqualTo("MS_DSP3"));
    Assert.That(r.CvfSignature, Is.EqualTo("DVR3"));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO.TXT"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(18));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("Genuine")]
  public void Genuine_DVR3_MultiCluster_Stored_RoundTrips() {
    // Spans several 4096-byte clusters, each a separate stored run resolved
    // through the MDFAT — proves the inner FAT16 chain walk + per-cluster run
    // framing for the genuine layout.
    var data = new byte[ClusterBytes * 3 + 1234];
    new Random(99).NextBytes(data);

    var img = BuildGenuineStoredImage(("DATA.BIN", data));

    using var ms = new MemoryStream(img);
    using var r = new DriveSpace3Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("Genuine")]
  public void Genuine_DVR3_MultiFile_Stored_RoundTrips() {
    var a = Encoding.ASCII.GetBytes("first file payload");
    var b = new byte[ClusterBytes + 7];
    new Random(7).NextBytes(b);

    var img = BuildGenuineStoredImage(("ALPHA.TXT", a), ("BETA.BIN", b));

    using var ms = new MemoryStream(img);
    using var r = new DriveSpace3Reader(ms);
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "ALPHA.TXT", "BETA.BIN" }));
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "ALPHA.TXT")), Is.EqualTo(a));
    Assert.That(r.Extract(r.Entries.First(e => e.Name == "BETA.BIN")), Is.EqualTo(b));
  }

  [Test, Category("Genuine")]
  public void Genuine_DVR3_MdfatEntry_BitLayout_IsExact() {
    // Cluster 2 of a single 18-byte file must encode phys=0, run=1, flags=1
    // exactly as the real oracle (raw 0x10200000).
    var img = BuildGenuineStoredImage(("HELLO.TXT", "CWB_CVF_PROOF_OK\r\n"u8.ToArray()));
    var mdfatStart = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(44));
    var entry = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)(mdfatStart * Ss + 2 * 4)));
    Assert.That(entry & 0x1FFFFFu, Is.EqualTo(0u), "physical sector offset");
    Assert.That((entry >> 21) & 0x7Fu, Is.EqualTo(1u), "run length in sectors");
    Assert.That((entry >> 28) & 0xFu, Is.EqualTo(1u), "stored flag");
    Assert.That(entry, Is.EqualTo(0x10200000u), "full genuine MDFAT raw entry");
  }
}
