using System.Reflection;
using System.Security.Cryptography;
using Compression.Lib;
using FileFormat.Lzh;

namespace Compression.Tests.Lzh;

/// <summary>
/// LHARK's own -lh7- (read as -lk7-, the way lhasa reads it) and self-extracting LHA archives,
/// from lhasa's test suite: LHARK-A 0.4d archives and an LHA 2.55e DOS self-extractor. Every member
/// must pass the CRC-16 its archiver recorded, and the GPL-2 text must match the same text other
/// archivers stored.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
public class LhaLharkAndSfxTests {
  private static byte[] Vector(string name) {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LzhVectors." + name)
      ?? throw new FileNotFoundException("Embedded LHA vector missing: " + name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static byte[] Gpl2() {
    using var reader = new LhaReader(new MemoryStream(Vector("lharc113_lh1.lzh")));
    return reader.ExtractEntry(reader.Entries.Single());
  }

  [Test]
  public void Given_LharksLh7_When_Read_Then_ItIsListedAsLk7AndDecodesToTheGpl2Text() {
    using var reader = new LhaReader(new MemoryStream(Vector("lhark04d_lh7.lzh")));
    var entry = reader.Entries.Single();
    Assert.That(entry.Method, Is.EqualTo(LhaConstants.MethodLk7));
    Assert.That(entry.FileName, Is.EqualTo("GPL-2"));
    Assert.That(reader.ExtractEntry(entry), Is.EqualTo(Gpl2()));
  }

  [Test]
  public void Given_ALongLharkStream_When_Read_Then_ItPassesItsCrc() {
    // 399,527 bytes: long copies (length codes 264-288) and far offsets across the 64 KiB window.
    using var reader = new LhaReader(new MemoryStream(Vector("lhark04d_lh7_long.lzh")));
    var entry = reader.Entries.Single();
    Assert.That(entry.Method, Is.EqualTo(LhaConstants.MethodLk7));
    Assert.That(entry.Crc16, Is.EqualTo(0x6DC1));
    Assert.That(reader.ExtractEntry(entry), Has.Length.EqualTo(399_527));
  }

  [Test]
  public void Given_ADamagedLharkStream_When_Extracted_Then_ItIsRefused() {
    var archive = Vector("lhark04d_lh7.lzh");
    using (var probe = new LhaReader(new MemoryStream(archive))) {
      var entry = probe.Entries.Single();
      archive[entry.DataOffset + entry.CompressedSize / 2] ^= 0x10;
    }
    using var reader = new LhaReader(new MemoryStream(archive));
    Assert.Catch<Exception>(() => reader.ExtractEntry(reader.Entries.Single()));
  }

  [Test]
  public void Given_AStandardLh7Member_When_Read_Then_ItStaysLh7() {
    // Only LHARK's level-1 headers with OS id ' ' are renamed; LHA's own -lh7- is untouched.
    var writer = new LhaWriter(LhaConstants.MethodLh7);
    writer.AddFile("a.txt", Gpl2());
    using var reader = new LhaReader(new MemoryStream(writer.ToArray()));
    Assert.That(reader.Entries.Single().Method, Is.EqualTo(LhaConstants.MethodLh7));
    Assert.That(reader.ExtractEntry(reader.Entries.Single()), Is.EqualTo(Gpl2()));
  }

  [Test]
  public void Given_ADosSelfExtractor_When_Read_Then_TheArchiveBehindTheStubIsFound() {
    using var reader = new LhaReader(new MemoryStream(Vector("lha255e_sfx.exe")));
    Assert.That(reader.ArchiveOffset, Is.EqualTo(1702));
    var entry = reader.Entries.Single();
    Assert.That(entry.FileName, Is.EqualTo("GPL-2"));
    Assert.That(entry.Crc16, Is.EqualTo(0xA33A));
    Assert.That(reader.ExtractEntry(entry), Is.EqualTo(Gpl2()));
  }

  [Test]
  public void Given_ADosSelfExtractorFile_When_Detected_Then_ItOpensAsAnSfxHoldingLha() {
    var path = Path.Combine(Path.GetTempPath(), "cwb-lha-sfx-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
    File.WriteAllBytes(path, Vector("lha255e_sfx.exe"));
    try {
      Assert.That(FormatDetector.Detect(path), Is.EqualTo(FormatDetector.Format.Sfx));
      var info = FormatDetector.GetSfxArchiveInfo(path);
      Assert.That(info, Is.Not.Null);
      Assert.That(info!.Value.ArchiveFormat, Is.EqualTo(FormatDetector.Format.Lzh));
      Assert.That(info.Value.Offset, Is.EqualTo(1702));
      Assert.That(ArchiveOperations.List(path, null).Select(e => e.Name), Is.EqualTo(new[] { "GPL-2" }));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void Given_ASelfExtractor_When_Edited_Then_ItIsRefusedAndLeftUntouched() {
    var original = Vector("lha255e_sfx.exe");
    using var stream = new MemoryStream();
    stream.Write(original);
    var descriptor = new LzhFormatDescriptor();
    Assert.Throws<NotSupportedException>(() => descriptor.Remove(stream, ["GPL-2"]));
    Assert.That(stream.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void Given_ASelfExtractor_When_ItsLayoutIsMapped_Then_TheStubIsReserved() {
    using var stream = new MemoryStream(Vector("lha255e_sfx.exe"));
    var first = LzhLayoutMap.Enumerate(stream).First();
    Assert.That(first.Offset, Is.Zero);
    Assert.That(first.Length, Is.EqualTo(1702));
    Assert.That(first.Kind, Is.EqualTo(Compression.Registry.DefragBlockKind.MetadataReserved));
  }

  [TestCase("lhark04d_lh7.lzh")]
  [TestCase("lhark04d_lh7_long.lzh")]
  [TestCase("lha255e_sfx.exe")]
  [Category("ExternalInterop")]
  public void Given_TheVectors_When_LhasaExtractsThem_Then_OurBytesMatch(string vector) {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("lha"))
      Assert.Ignore("lhasa's lha is not available (WSL: sudo apt install -y lhasa).");
    var dir = Directory.CreateTempSubdirectory("cwb-lha-").FullName;
    try {
      var archive = Path.Combine(dir, vector);
      File.WriteAllBytes(archive, Vector(vector));
      var into = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
      var x = FsInteropToolbox.RunWsl($"cd {FsInteropToolbox.WinToWsl(into)} && lha xq {FsInteropToolbox.WinToWsl(archive)}");
      Assert.That(x.ExitCode, Is.Zero, x.StdOut + x.StdErr);
      using var reader = new LhaReader(new MemoryStream(Vector(vector)));
      foreach (var entry in reader.Entries)
        Assert.That(reader.ExtractEntry(entry), Is.EqualTo(File.ReadAllBytes(Path.Combine(into, entry.FileName))), entry.FileName);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Given_APlainArchiveWithADamagedFirstHeader_When_Read_Then_TheDamageIsReportedNotSkipped() {
    var archive = Vector("lharc113_lh1.lzh");
    archive[1] ^= 0xFF; // header checksum
    Assert.Throws<InvalidDataException>(() => _ = new LhaReader(new MemoryStream(archive)));
  }
}

/// <summary>
/// LHA fills its history with spaces before the first byte (LHa for UNIX, lhasa), so a copy may
/// reach before the start of the data. This builds such an -lh5- stream by hand.
/// </summary>
[TestFixture]
public class LhaHistoryTests {

  [Test]
  public void Given_ARepetitionReachingBeforeTheStart_When_Decoded_Then_TheHistoryReadsAsSpaces() {
    using var reader = new LhaReader(new MemoryStream(BuildSpaceReferenceArchive()));
    Assert.That(reader.ExtractEntry(reader.Entries.Single()), Is.EqualTo("   x"u8.ToArray()));
  }

  /// <summary>
  /// A level-0 -lh5- archive of "   x" whose first token copies 3 bytes from distance 3, i.e. from
  /// the pre-filled history. One block of two commands: a T tree with lengths [1, 2, 0, 2], a C tree
  /// giving 'x' and code 256 (copy of 3) one bit each, and a single-symbol position tree (slot 2).
  /// </summary>
  private static byte[] BuildSpaceReferenceArchive() {
    var bits = new List<int>();
    void Put(int value, int count) { for (var i = count - 1; i >= 0; --i) bits.Add((value >> i) & 1); }
    void Zeros(int count) {
      while (count > 0) {
        if (count >= 3) {
          var run = Math.Min(count, 18);
          Put(0b10, 2);           // T symbol 1: a run of 3 + (4 bits) zero lengths
          Put(run - 3, 4);
          count -= run;
        } else {
          Put(0b0, 1);            // T symbol 0: one zero length
          --count;
        }
      }
    }

    Put(2, 16);                   // two commands in the block
    Put(4, 5);                    // T tree: four lengths
    Put(1, 3); Put(2, 3); Put(0, 3);
    Put(0, 2);                    // nothing skipped after the third
    Put(2, 3);                    // T lengths 1, 2, 0, 2 -> codes "0", "10", -, "11"
    Put(257, 9);                  // C tree: 257 lengths
    Zeros(120);
    Put(0b11, 2);                 // T symbol 3 -> length 1 for 'x' (120)
    Zeros(135);
    Put(0b11, 2);                 // length 1 for code 256 (copy of 3)
    Put(0, 4); Put(2, 4);         // position tree: the single slot 2
    Put(1, 1);                    // code 256: copy 3 bytes
    Put(0, 1);                    // slot 2's extra bit: distance - 1 = 2
    Put(0, 1);                    // 'x'

    var payload = new byte[(bits.Count + 7) / 8];
    for (var i = 0; i < bits.Count; ++i)
      if (bits[i] != 0)
        payload[i / 8] |= (byte)(0x80 >> (i % 8));

    var data = "   x"u8.ToArray();
    var crc = Compression.Core.Checksums.Crc16.Compute(data);
    var name = "s.txt"u8.ToArray();
    var header = new List<byte> { 0, 0 };
    header.AddRange("-lh5-"u8.ToArray());
    header.AddRange(BitConverter.GetBytes(payload.Length));
    header.AddRange(BitConverter.GetBytes(data.Length));
    header.AddRange(BitConverter.GetBytes(15393u << 16));  // DOS date 2010-01-01, time 00:00
    header.Add(0x20);                                      // attribute
    header.Add(0);                                         // level 0
    header.Add((byte)name.Length);
    header.AddRange(name);
    header.AddRange(BitConverter.GetBytes(crc));
    header[0] = (byte)(header.Count - 2);
    byte sum = 0;
    for (var i = 2; i < header.Count; ++i)
      sum += header[i];
    header[1] = sum;
    return [.. header, .. payload, 0];
  }
}
