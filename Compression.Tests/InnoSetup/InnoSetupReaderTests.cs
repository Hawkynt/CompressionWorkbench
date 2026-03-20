using System.Buffers.Binary;
using System.Text;
using Compression.Core.Deflate;
using FileFormat.InnoSetup;

namespace Compression.Tests.InnoSetup;

/// <summary>
/// Tests for <see cref="InnoSetupReader"/>.
/// All tests use synthetic PE + Inno Setup payloads constructed in-memory;
/// no external installer binary is required.
/// </summary>
[TestFixture]
public class InnoSetupReaderTests {

  // ---------------------------------------------------------------------------
  // Helpers — build minimal synthetic PE + Inno overlay
  // ---------------------------------------------------------------------------

  /// <summary>
  /// Builds a minimal 32-bit PE stub followed by an Inno Setup modern overlay.
  /// The Setup.0 payload is a zlib-compressed block containing a set of Pascal
  /// strings that represent fake file entries.
  /// </summary>
  private static byte[] BuildSyntheticInstaller(
      string innoVersion,
      IEnumerable<string> filenames,
      bool useLegacySig = false) {

    using var ms = new MemoryStream();

    // --- Minimal PE stub ---------------------------------------------------
    // MZ header
    ms.WriteByte((byte)'M');
    ms.WriteByte((byte)'Z');
    // Pad to offset 0x3C (58 bytes of padding)
    ms.Write(new byte[0x3A]);
    // e_lfanew = 0x40 (64)
    Span<byte> ptrBuf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(ptrBuf, 0x40);
    ms.Write(ptrBuf);

    // PE signature at 0x40
    ms.Write("PE\0\0"u8);
    // COFF header (20 bytes)
    // Machine = 0x014C (x86), NumberOfSections = 1
    Span<byte> coff = stackalloc byte[20];
    BinaryPrimitives.WriteUInt16LittleEndian(coff,      0x014C); // Machine
    BinaryPrimitives.WriteUInt16LittleEndian(coff[2..], 1);      // NumSections
    // SizeOfOptionalHeader = 0 (we skip the optional header entirely)
    BinaryPrimitives.WriteUInt16LittleEndian(coff[16..], 0);
    ms.Write(coff);

    // Section table (40 bytes for one section)
    // Name = ".text\0\0\0", VirtualSize = 0, VirtualAddress = 0,
    // SizeOfRawData = 16, PointerToRawData = 0x40 + 4 + 20 + 40 = 0x80
    var sectionRawPtr = (uint)(0x40 + 4 + 20 + 40); // = 0x80
    var sectionRawSize = 16u;
    Span<byte> sec = stackalloc byte[40];
    ".text\0\0\0"u8.CopyTo(sec);
    BinaryPrimitives.WriteUInt32LittleEndian(sec[16..], sectionRawSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sec[20..], sectionRawPtr);
    ms.Write(sec);

    // Raw section data (16 zero bytes at 0x80)
    ms.Write(new byte[16]);

    // Overlay starts at sectionRawPtr + sectionRawSize = 0x80 + 0x10 = 0x90
    // Pad to 0x90 if needed
    while (ms.Length < 0x90)
      ms.WriteByte(0);

    // --- Inno Setup overlay ------------------------------------------------
    if (useLegacySig) {
      // Legacy: "rDlPtS" + 2 version bytes (major, minor)
      ms.Write("rDlPtS"u8);
      ms.WriteByte(1); // major
      ms.WriteByte(6); // minor
    } else {
      // Modern: "Inno Setup Setup Data (" + version + ")\0"
      var sigStr = $"Inno Setup Setup Data ({innoVersion})\0";
      ms.Write(Encoding.ASCII.GetBytes(sigStr));
    }

    // Build the decompressed Setup.0 payload: Pascal strings for each filename
    var decompressedHeader = BuildPascalStringPayload(filenames);

    // Compress with zlib (raw Deflate with zlib framing)
    var zlibData = CompressZlib(decompressedHeader);

    // 4-byte header CRC (we write zero — the reader does not verify)
    Span<byte> crcBuf = stackalloc byte[4];
    ms.Write(crcBuf);

    // 4-byte compressed size
    Span<byte> szBuf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(szBuf, (uint)zlibData.Length);
    ms.Write(szBuf);

    // Compressed Setup.0 data
    ms.Write(zlibData);

    return ms.ToArray();
  }

  private static byte[] BuildPascalStringPayload(IEnumerable<string> values) {
    using var ms = new MemoryStream();
    var lenBuf = new byte[4];
    foreach (var v in values) {
      var bytes = Encoding.UTF8.GetBytes(v);
      BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)bytes.Length);
      ms.Write(lenBuf);
      ms.Write(bytes);
    }
    return ms.ToArray();
  }

  private static byte[] CompressZlib(byte[] data) {
    // Compress with DeflateCompressor then wrap in zlib framing (CMF+FLG header + Adler-32 trailer)
    var deflateBytes = DeflateCompressor.Compress(data);

    // Compute Adler-32
    uint a = 1, b = 0;
    foreach (var byt in data) {
      a = (a + byt) % 65521;
      b = (b + a) % 65521;
    }
    var adler32 = (b << 16) | a;

    using var zlibMs = new MemoryStream();
    zlibMs.WriteByte(0x78); // CMF: method=8, windowBits=7 (32KB)
    zlibMs.WriteByte(0x9C); // FLG: (0x78*256 + FLG) % 31 == 0 → FLG=0x9C
    zlibMs.Write(deflateBytes);
    Span<byte> adlerBuf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(adlerBuf, adler32);
    zlibMs.Write(adlerBuf);
    return zlibMs.ToArray();
  }

  // ---------------------------------------------------------------------------
  // Happy-path tests
  // ---------------------------------------------------------------------------

  [Test]
  [Category("HappyPath")]
  public void Constructor_ModernSignature_DetectsVersion() {
    var installer = BuildSyntheticInstaller("5.5.3", ["setup.exe", "readme.txt"]);
    using var ms = new MemoryStream(installer);
    var reader = new InnoSetupReader(ms);
    Assert.That(reader.Version, Is.EqualTo("5.5.3"));
  }

  [Test]
  [Category("HappyPath")]
  public void Constructor_Version6_DetectsVersion() {
    var installer = BuildSyntheticInstaller("6.2.0", ["app.exe"]);
    using var ms = new MemoryStream(installer);
    var reader = new InnoSetupReader(ms);
    Assert.That(reader.Version, Is.EqualTo("6.2.0"));
  }

  [Test]
  [Category("HappyPath")]
  public void Constructor_LegacySignature_DetectsVersionBytes() {
    var installer = BuildSyntheticInstaller("", [], useLegacySig: true);
    using var ms = new MemoryStream(installer);
    var reader = new InnoSetupReader(ms);
    // Legacy version is encoded as "major.minor" from the two bytes after the magic
    Assert.That(reader.Version, Is.EqualTo("1.6"));
  }

  [Test]
  [Category("HappyPath")]
  public void Entries_ContainsFilenamesFromHeader() {
    var filenames = new[] { "notepad.exe", "readme.txt", "license.rtf" };
    var installer = BuildSyntheticInstaller("5.5.3", filenames);
    using var ms = new MemoryStream(installer);
    var reader = new InnoSetupReader(ms);

    var entryNames = reader.Entries.Select(e => e.FileName).ToList();
    // Each filename should appear in the parsed entries
    foreach (var f in filenames)
      Assert.That(entryNames, Has.Member(f),
        $"Expected entry '{f}' to be present in parsed entries.");
  }

  [Test]
  [Category("HappyPath")]
  public void Entries_IsReadOnlyList() {
    var installer = BuildSyntheticInstaller("5.5.3", ["app.exe"]);
    using var ms = new MemoryStream(installer);
    var reader = new InnoSetupReader(ms);
    Assert.That(reader.Entries, Is.InstanceOf<IReadOnlyList<InnoSetupEntry>>());
  }

  [Test]
  [Category("HappyPath")]
  public void Version_IsNotNullOrEmpty() {
    var installer = BuildSyntheticInstaller("6.0.5", ["a.dll"]);
    using var ms = new MemoryStream(installer);
    var reader = new InnoSetupReader(ms);
    Assert.That(reader.Version, Is.Not.Null.And.Not.Empty);
  }

  [Test]
  [Category("HappyPath")]
  public void Entries_InnoConstantPaths_AreIncluded() {
    // Inno Setup uses constant strings like {app}\program.exe as dest dirs
    var filenames = new[] { "{app}\\program.exe", "{sys}\\helper.dll" };
    var installer = BuildSyntheticInstaller("5.6.1", filenames);
    using var ms = new MemoryStream(installer);
    var reader = new InnoSetupReader(ms);

    var entryNames = reader.Entries.Select(e => e.FileName).ToList();
    Assert.That(entryNames, Has.Member("{app}\\program.exe").Or.Member("{app}\\program.exe".Replace('\\', '/')));
  }

  // ---------------------------------------------------------------------------
  // Entry record tests
  // ---------------------------------------------------------------------------

  [Test]
  [Category("HappyPath")]
  public void Entry_DefaultSizeFields_AreMinusOne() {
    var installer = BuildSyntheticInstaller("5.5.3", ["setup.exe"]);
    using var ms = new MemoryStream(installer);
    var reader = new InnoSetupReader(ms);

    // All entries from heuristic parsing have unknown sizes
    Assert.That(reader.Entries.All(e => e.Size == -1 && e.CompressedSize == -1),
      Is.True, "Heuristically parsed entries should report Size=-1 and CompressedSize=-1.");
  }

  [Test]
  [Category("HappyPath")]
  public void Entry_IsDirectory_IsFalseForFiles() {
    var installer = BuildSyntheticInstaller("5.5.3", ["app.exe", "data.bin"]);
    using var ms = new MemoryStream(installer);
    var reader = new InnoSetupReader(ms);

    Assert.That(reader.Entries.All(e => !e.IsDirectory),
      Is.True, "All heuristically parsed entries should have IsDirectory=false.");
  }

  // ---------------------------------------------------------------------------
  // Extract — not supported for heuristically parsed entries
  // ---------------------------------------------------------------------------

  [Test]
  [Category("Exception")]
  public void Extract_ThrowsNotSupportedException() {
    var installer = BuildSyntheticInstaller("5.5.3", ["app.exe"]);
    using var ms = new MemoryStream(installer);
    var reader = new InnoSetupReader(ms);

    // Heuristically parsed entries carry CompressedSize=-1, so Extract throws
    if (reader.Entries.Count > 0)
      Assert.Throws<NotSupportedException>(() => reader.Extract(reader.Entries[0]));
  }

  [Test]
  [Category("Exception")]
  public void Extract_DirectoryEntry_ThrowsInvalidOperationException() {
    var dirEntry = new InnoSetupEntry("{app}", "", -1, -1, IsDirectory: true);
    // We need a reader to call Extract on; use a minimal valid installer
    var installer = BuildSyntheticInstaller("5.5.3", []);
    using var ms = new MemoryStream(installer);
    var reader = new InnoSetupReader(ms);

    Assert.Throws<InvalidOperationException>(() => reader.Extract(dirEntry));
  }

  // ---------------------------------------------------------------------------
  // Error handling
  // ---------------------------------------------------------------------------

  [Test]
  [Category("Exception")]
  public void Constructor_NullStream_ThrowsArgumentNullException() {
    Assert.Throws<ArgumentNullException>(() => _ = new InnoSetupReader(null!));
  }

  [Test]
  [Category("Exception")]
  public void Constructor_RandomBytes_ThrowsInvalidDataException() {
    var rng = new Random(42);
    var junk = new byte[512];
    rng.NextBytes(junk);
    // Ensure it does not start with MZ to skip PE parsing
    junk[0] = 0x00;

    using var ms = new MemoryStream(junk);
    Assert.Throws<InvalidDataException>(() => _ = new InnoSetupReader(ms));
  }

  [Test]
  [Category("Exception")]
  public void Constructor_MzButNoInnoSig_ThrowsInvalidDataException() {
    // Build a minimal MZ file with no Inno signature
    var data = new byte[512];
    data[0] = (byte)'M';
    data[1] = (byte)'Z';
    // e_lfanew points past end of buffer — FindSignature will scan and fail
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x3C), 0x100);

    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new InnoSetupReader(ms));
  }

  // ---------------------------------------------------------------------------
  // Signature position — overlay at non-zero offset
  // ---------------------------------------------------------------------------

  [Test]
  [Category("HappyPath")]
  public void Constructor_SignatureInOverlay_ParsedCorrectly() {
    // The PE overlay is at 0x90 in our synthetic installer; ensure the reader
    // finds the signature there and not at position 0.
    var installer = BuildSyntheticInstaller("5.4.2", ["setup.exe", "unins000.exe"]);
    using var ms = new MemoryStream(installer);
    var reader = new InnoSetupReader(ms);

    Assert.Multiple(() => {
      Assert.That(reader.Version, Is.EqualTo("5.4.2"));
      Assert.That(reader.Entries, Is.Not.Empty);
    });
  }
}
