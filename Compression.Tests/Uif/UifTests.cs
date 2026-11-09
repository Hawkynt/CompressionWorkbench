using System.Buffers.Binary;
using System.Text;
using FileFormat.Uif;

namespace Compression.Tests.Uif;

[TestFixture]
public class UifTests {
  /// <summary>Builds a version 1, type 8 UIF from blocks given as (sector, sectors, type, payload).</summary>
  internal static byte[] Build(uint sectors, uint sectorSize, (uint Sector, uint Count, uint Type, byte[] Payload)[] blocks,
      bool compressTable = true, ushort version = 1, ushort imageType = 8, byte key1 = 0, byte key2 = 0, uint lastDiff = 0) {
    using var ms = new MemoryStream();
    var table = new byte[blocks.Length * UifImage.EntrySize];
    for (var i = 0; i < blocks.Length; ++i) {
      var (sector, count, type, payload) = blocks[i];
      var e = table.AsSpan(i * UifImage.EntrySize);
      BinaryPrimitives.WriteUInt64LittleEndian(e, type == 3 ? 0 : (ulong)ms.Position);
      BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)payload.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(e[12..], sector);
      BinaryPrimitives.WriteUInt32LittleEndian(e[16..], count);
      BinaryPrimitives.WriteUInt32LittleEndian(e[20..], type);
      ms.Write(payload);
    }
    var tableAt = ms.Position;
    var stored = compressTable ? Zlib(table) : table;
    var head = new byte[16];
    "blhr"u8.CopyTo(head);
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), (uint)stored.Length + 8);
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), compressTable ? 1u : 0u);
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), (uint)blocks.Length);
    ms.Write(head);
    ms.Write(stored);
    var t = new byte[UifImage.TrailerSize];
    "bbis"u8.CopyTo(t);
    BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(4), UifImage.TrailerSize);
    BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(8), version);
    BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(10), imageType);
    BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(16), sectors);
    BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(20), sectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(24), lastDiff);
    BinaryPrimitives.WriteUInt64LittleEndian(t.AsSpan(28), (ulong)tableAt);
    BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(36), (uint)(stored.Length + 16 + UifImage.TrailerSize));
    t[56] = key1;
    t[57] = key2;
    ms.Write(t);
    return ms.ToArray();
  }

  internal static byte[] Zlib(byte[] data) {
    using var ms = new MemoryStream();
    using (var z = new FileFormat.Zlib.ZlibStream(ms, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true))
      z.Write(data);
    return ms.ToArray();
  }

  private static byte[] Pattern(int length, int seed) {
    var d = new byte[length];
    new Random(seed).NextBytes(d);
    return d;
  }

  private static byte[] Extract(byte[] uif) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-uif-" + Guid.NewGuid().ToString("N"));
    try {
      new UifFormatDescriptor().Extract(new MemoryStream(uif), dir, null, null);
      return File.ReadAllBytes(Path.Combine(dir, "image.iso"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void GivenStoredZeroAndCompressedRuns_WhenExtracted_ThenEachLandsAtItsSector() {
    var a = Pattern(2 * 2048, 1);
    var c = Pattern(3 * 2048, 2);
    var uif = Build(8, 2048, [(0, 2, 1, a), (2, 3, 3, []), (5, 3, 5, Zlib(c))]);
    var iso = Extract(uif);
    Assert.That(iso, Has.Length.EqualTo(8 * 2048));
    Assert.That(iso.AsSpan(0, 4096).SequenceEqual(a), Is.True);
    Assert.That(iso.AsSpan(4096, 3 * 2048).ContainsAnyExcept((byte)0), Is.False);
    Assert.That(iso.AsSpan(5 * 2048).SequenceEqual(c), Is.True);
  }

  [Test]
  public void GivenAShortStoredRun_WhenExtracted_ThenItIsZeroPadded() {
    var iso = Extract(Build(2, 2048, [(0, 2, 1, [1, 2, 3])]));
    Assert.That(iso.AsSpan(0, 3).ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
    Assert.That(iso.AsSpan(3).ContainsAnyExcept((byte)0), Is.False);
  }

  [Test]
  public void GivenAnUncompressedTableAndAPartialLastSector_WhenExtracted_ThenTheImageIsCutThere() {
    var a = Pattern(2 * 2048, 3);
    var iso = Extract(Build(2, 2048, [(0, 2, 1, a)], compressTable: false, lastDiff: 100));
    Assert.That(iso, Is.EqualTo(a[..(2048 + 100)]));
  }

  [Test]
  public void GivenAnImage_WhenListed_ThenMetadataAndTheImageSizeAppear() {
    var entries = new UifFormatDescriptor().List(new MemoryStream(Build(4, 2048, [(0, 4, 3, [])])), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini", "image.iso" }));
    Assert.That(entries[1].OriginalSize, Is.EqualTo(4 * 2048));
  }

  [TestCase(2, (byte)1, (byte)0)]
  [TestCase(1, (byte)0, (byte)2)]
  public void GivenAnEncryptedImage_WhenListed_ThenItIsRefusedWithoutThrowing(int version, byte key1, byte key2) {
    var uif = Build(1, 2048, [(0, 1, 3, [])], version: (ushort)version, key1: key1, key2: key2);
    Assert.Throws<NotSupportedException>(() => UifImage.Read(new MemoryStream(uif)));
    Assert.That(new UifFormatDescriptor().List(new MemoryStream(uif), null).Single().Name, Is.EqualTo("FULL.uif"));
  }

  [Test]
  public void GivenAVersion1KeyByte_WhenRead_ThenItIsIgnoredAsTheFormatDoes()
    => Assert.That(UifImage.Read(new MemoryStream(Build(1, 2048, [(0, 1, 3, [])], key1: 5))).Blocks, Has.Count.EqualTo(1));

  [TestCase(3, 8)]
  [TestCase(1, 9)]
  public void GivenAnUnsupportedVersionOrType_WhenRead_ThenNotSupported(int version, int type)
    => Assert.Throws<NotSupportedException>(() => UifImage.Read(new MemoryStream(Build(1, 2048, [(0, 1, 3, [])], version: (ushort)version, imageType: (ushort)type))));

  [Test]
  public void GivenAnUnknownBlockType_WhenRead_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => UifImage.Read(new MemoryStream(Build(1, 2048, [(0, 1, 7, [])]))));

  [Test]
  public void GivenABlockPastTheLastSector_WhenRead_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => UifImage.Read(new MemoryStream(Build(1, 2048, [(0, 2, 3, [])]))));

  [Test]
  public void GivenACorruptCompressedRun_WhenExtracted_ThenInvalidDataAndNoImageIsLeft() {
    var payload = Zlib(Pattern(2048, 4));
    payload[^1] ^= 0xFF; // the Adler-32 no longer matches
    var uif = Build(1, 2048, [(0, 1, 5, payload)]);
    var dir = Path.Combine(Path.GetTempPath(), "cwb-uifx-" + Guid.NewGuid().ToString("N"));
    try {
      Assert.Throws<InvalidDataException>(() => new UifFormatDescriptor().Extract(new MemoryStream(uif), dir, null, null));
      Assert.That(File.Exists(Path.Combine(dir, "image.iso")), Is.False);
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void GivenNoTrailer_WhenListed_ThenListFallsBack()
    => Assert.That(new UifFormatDescriptor().List(new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 200))), null).Single().Name, Is.EqualTo("FULL.uif"));
}

/// <summary>
/// uif2iso 0.1.7c (Luigi Auriemma, GPL, built from its published source) as the oracle on two real
/// MagicISO images published on archive.org ("Quake Shareware" and "Doom II German"): the ISO we
/// extract equals the one uif2iso writes. The images come from <c>CWB_UIF_DIR</c> and uif2iso from
/// <c>CWB_UIF2ISO</c> (WSL paths on Windows); skipped when unset unless <c>CWB_REQUIRE_UIF=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class UifExternalConformanceTests {
  private static (string Dir, string Tool) Require() {
    var dir = Environment.GetEnvironmentVariable("CWB_UIF_DIR");
    var tool = Environment.GetEnvironmentVariable("CWB_UIF2ISO");
    if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(tool) && Directory.Exists(dir) && FsInteropToolbox.WslAvailable) return (dir, tool);
    const string hint = "UIF samples or uif2iso not configured: set CWB_UIF_DIR and CWB_UIF2ISO.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_UIF") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
    return default;
  }

  /// <summary>A path for the shell, quoted: the sample names contain spaces.</summary>
  private static string Quote(string path) => OperatingSystem.IsWindows() ? FsInteropToolbox.WinToWsl(path) : $"'{path}'";

  [TestCase("Quake Shareware.UIF")]
  [TestCase("Doom II German.UIF")]
  public void GivenARealMagicIsoImage_WhenExtracted_ThenItEqualsUif2isosIso(string name) {
    var (dir, tool) = Require();
    var uif = Path.Combine(dir, name);
    var tmp = Path.Combine(Path.GetTempPath(), "cwb-uifo-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      var theirs = Path.Combine(tmp, "theirs.iso");
      // uif2iso asks before overwriting and picks the extension itself; feed it "y" and a fresh name.
      var r = FsInteropToolbox.RunWsl($"yes | '{tool}' {Quote(uif)} {Quote(theirs)}");
      TestContext.Out.WriteLine($"{r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
      Assert.That(File.Exists(theirs), Is.True, "uif2iso wrote an ISO");

      var ours = Path.Combine(tmp, "ours");
      using (var fs = File.OpenRead(uif))
        new UifFormatDescriptor().Extract(fs, ours, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(ours, "image.iso")), Is.EqualTo(File.ReadAllBytes(theirs)));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }
}
