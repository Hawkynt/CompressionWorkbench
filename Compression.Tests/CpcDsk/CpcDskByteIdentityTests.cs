#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.CpcDsk;

namespace Compression.Tests.CpcDsk;

/// <summary>
/// Byte-identity contract tests for the CPC DSK container.
///
/// <para>The Amstrad CPC DSK container is a 256-byte Disk Info header
/// followed by per-track blocks (256-byte Track Info Block + sector data).
/// CpcDskModifier mutates the directory area on track 0 (offsets ≥ 512) and
/// allocates data sectors on tracks 1+. The Disk Info header at [0, 256) is
/// pure container metadata (creator string, track count, geometry) and must
/// survive byte-identical across every Add/Remove call — that's what every
/// CPC emulator and AMSDOS tool keys off to dispatch the image.</para>
/// </summary>
[TestFixture]
public class CpcDskByteIdentityTests {

  // Disk Info header is the first 256 bytes per the CPC DSK spec.
  private const int DiskInfoHeaderSize = 256;

  // Track Info Block for track 0 sits at offset 256 (next 256 bytes).
  private const int TrackInfoBlockSize = 256;
  private const long Track0TibOffset = 256;

  [Test, Category("ContractLock")]
  public void CpcDsk_DiskInfoHeader_Survives_Add_ByteIdentical() {
    var ms = BuildEmptyImage();
    var preHeader = ReadAt(ms, 0, DiskInfoHeaderSize);

    CpcDskModifier.AddFile(ms, "HELLO.TXT", "world"u8.ToArray());

    var postHeader = ReadAt(ms, 0, DiskInfoHeaderSize);
    Assert.That(postHeader, Is.EqualTo(preHeader),
      "Disk Info header [0, 256) (creator + track count + geometry) must survive byte-identical");
  }

  [Test, Category("ContractLock")]
  public void CpcDsk_DiskInfoHeader_Survives_Remove_ByteIdentical() {
    var ms = BuildEmptyImage();
    CpcDskModifier.AddFile(ms, "DROP.TXT", "drop"u8.ToArray());
    var preHeader = ReadAt(ms, 0, DiskInfoHeaderSize);

    var removed = CpcDskModifier.RemoveFile(ms, "DROP.TXT");
    Assert.That(removed, Is.True);

    var postHeader = ReadAt(ms, 0, DiskInfoHeaderSize);
    Assert.That(postHeader, Is.EqualTo(preHeader),
      "Disk Info header must survive Remove byte-identical");
  }

  [Test, Category("ContractLock")]
  public void CpcDsk_Track0_TrackInfoBlock_Survives_Add_ByteIdentical() {
    var ms = BuildEmptyImage();
    var preTib = ReadAt(ms, Track0TibOffset, TrackInfoBlockSize);

    CpcDskModifier.AddFile(ms, "DATA.BIN", "abcd"u8.ToArray());

    var postTib = ReadAt(ms, Track0TibOffset, TrackInfoBlockSize);
    Assert.That(postTib, Is.EqualTo(preTib),
      "Track 0 Track Info Block (track/side/sector IDs + ST1/ST2 + sector sizes) " +
      "must survive byte-identical (only directory bytes mutate)");
  }

  [Test, Category("ContractLock")]
  public void CpcDsk_Descriptor_Add_PreservesDiskInfoHeader() {
    var ms = BuildEmptyImage();
    var preHeader = ReadAt(ms, 0, DiskInfoHeaderSize);

    var desc = new CpcDskFormatDescriptor();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "payload"u8.ToArray());
      ((IArchiveModifiable)desc).Add(ms, [new ArchiveInputInfo(tmp, "TEST.TXT", false)]);
    } finally {
      File.Delete(tmp);
    }

    var postHeader = ReadAt(ms, 0, DiskInfoHeaderSize);
    Assert.That(postHeader, Is.EqualTo(preHeader),
      "Descriptor.Add must preserve Disk Info header byte-identical " +
      "(delegates to CpcDskModifier.AddFile)");
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

  private static MemoryStream BuildEmptyImage() {
    var ms = new MemoryStream();
    using (var w = new CpcDskWriter(ms, leaveOpen: true,
                tracks: 5, sides: 1, sectorsPerTrack: 9, sectorSize: 512))
      w.Finish();
    ms.Position = 0;
    return ms;
  }
}
