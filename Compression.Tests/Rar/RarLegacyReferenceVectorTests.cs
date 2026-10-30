using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// RAR 1.5 and 2.0 archives written by the original archivers (see Rar/LegacyVectors/README.md),
/// extracted and compared with what 7-Zip extracts from them.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
public class RarLegacyReferenceVectorTests {
  private static IEnumerable<TestCaseData> ManifestRows() {
    foreach (var line in ReadResourceText("manifest.tsv").Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
      var columns = line.TrimEnd('\r').Split('\t');
      yield return new TestCaseData(columns[0], columns[1], long.Parse(columns[2]), columns[3])
        .SetName($"Given_{columns[0]}_When_{columns[1]}IsExtracted_Then_ItMatches7Zip");
    }
  }

  [TestCaseSource(nameof(ManifestRows))]
  public void Given_ARealArchive_When_AnEntryIsExtracted_Then_ItMatches7Zip(string archive, string entryName, long size, string sha256) {
    using var reader = new RarReader(new MemoryStream(ReadResource(archive)));

    var index = FindEntry(reader, entryName);
    var data = reader.Extract(index);

    Assert.That(data.LongLength, Is.EqualTo(size));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(sha256));
  }

  [TestCase("sharpcompress-rar15.rar")]
  [TestCase("sharpcompress-rar20.rar")]
  [TestCase("winrar290-solid.rar")]
  [TestCase("winrar290-solid-mixed.rar")]
  public void Given_ARealArchive_When_EntriesAreExtractedInReverseOrder_Then_EachStillMatches(string archive) {
    var expected = ManifestRows().Select(r => r.Arguments).Where(a => (string)a[0]! == archive).ToList();
    using var reader = new RarReader(new MemoryStream(ReadResource(archive)));
    Assert.That(reader.IsRar4, Is.True);

    for (var i = expected.Count - 1; i >= 0; --i) {
      var data = reader.Extract(FindEntry(reader, (string)expected[i][1]!));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo((string)expected[i][3]!));
    }
  }

  [TestCase("sharpcompress-rar15.rar", "TECT.TXT")]
  [TestCase("sharpcompress-rar20.rar", "exe/test.exe")]
  public void Given_ARealArchiveWithACorruptedPackedByte_When_Extracted_Then_ItThrowsInsteadOfReturningWrongBytes(string archive, string entryName) {
    var bytes = ReadResource(archive);
    using (var probe = new RarReader(new MemoryStream(bytes))) {
      var entry = probe.Entries[FindEntry(probe, entryName)];
      Assert.That(entry.CompressedSize, Is.GreaterThan(64));
    }

    // Flip a byte in the middle of the entry's packed data: the decoder must refuse or the CRC must catch it.
    var offset = LocatePackedData(bytes, archive, entryName);
    bytes[offset] ^= 0x5A;

    using var reader = new RarReader(new MemoryStream(bytes));
    Assert.That(() => reader.Extract(FindEntry(reader, entryName)), Throws.InstanceOf<InvalidDataException>());
  }

  [TestCase("winrar290-audio.rar", "mono8.wav")]
  [TestCase("winrar290-audio.rar", "stereo16.wav")]
  [TestCase("winrar290-audio.rar", "quad8.wav")]
  public void Given_AWinRar290MultimediaEntry_When_ItsStreamIsInspected_Then_ItOpensWithAnAudioBlock(string archive, string entryName) {
    // The manifest pins the decoded bytes; this pins that the multimedia path is what decoded them.
    var bytes = ReadResource(archive);
    var header = FindFileHeader(bytes, archive, entryName);
    var expected = ManifestRows().Select(r => r.Arguments).Single(a => (string)a[0]! == archive && (string)a[1]! == entryName);

    var decoder = new Compression.Core.Dictionary.Rar.Rar2Decoder();
    var data = decoder.Decompress(bytes.AsSpan(header.DataStart, header.PackSize), (int)(long)expected[2]!);

    Assert.That(decoder.AudioTableCount, Is.GreaterThan(0));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo((string)expected[3]!));
  }

  [Test]
  public void Given_TheSolidMixedArchive_When_ItsStreamIsDecodedInOrder_Then_ItSwitchesFromLzToAudioTables() {
    const string archive = "winrar290-solid-mixed.rar";
    var bytes = ReadResource(archive);
    var decoder = new Compression.Core.Dictionary.Rar.Rar2Decoder();
    var audioTablesBefore = new List<int>();
    var rows = ManifestRows().Select(r => r.Arguments).Where(a => (string)a[0]! == archive).ToList();
    using var reader = new RarReader(new MemoryStream(bytes));
    var archiveOrder = reader.Entries.Select(e => e.Name).ToList();
    foreach (var row in rows.OrderBy(r => archiveOrder.IndexOf((string)r[1]!))) {
      var header = FindFileHeader(bytes, archive, (string)row[1]!);
      audioTablesBefore.Add(decoder.AudioTableCount);
      var data = decoder.Decompress(bytes.AsSpan(header.DataStart, header.PackSize), (int)(long)row[2]!);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo((string)row[3]!), (string)row[1]!);
    }

    Assert.That(audioTablesBefore[0], Is.Zero);
    Assert.That(decoder.AudioTableCount, Is.GreaterThan(0));
  }

  [TestCase("winrar290-solid.rar")]
  [TestCase("winrar290-solid-mixed.rar")]
  public void Given_AWinRar290SolidArchive_When_HeadersAreRead_Then_EveryEntryAfterTheFirstContinuesTheStream(string archive) {
    var bytes = ReadResource(archive);
    using var reader = new RarReader(new MemoryStream(bytes));
    var flags = reader.Entries.Select(e => FindFileHeader(bytes, archive, e.Name).Flags).ToList();
    Assert.That(flags, Has.Count.GreaterThan(1));
    Assert.That(flags.Skip(1).All(f => (f & 0x10) != 0), Is.True);
  }

  private static int LocatePackedData(byte[] bytes, string archive, string entryName) {
    var header = FindFileHeader(bytes, archive, entryName);
    return header.DataStart + header.PackSize / 2;
  }

  /// <summary>Walks the RAR 4 block chain to the entry's file header; its packed data follows it.</summary>
  private static (int DataStart, int PackSize, ushort Flags) FindFileHeader(byte[] bytes, string archive, string entryName) {
    var position = 7;
    while (position + 7 <= bytes.Length) {
      var headType = bytes[position + 2];
      var headFlags = BitConverter.ToUInt16(bytes, position + 3);
      var headSize = BitConverter.ToUInt16(bytes, position + 5);
      long addSize = (headFlags & 0x8000) != 0 || headType == 0x74 ? BitConverter.ToUInt32(bytes, position + 7) : 0;
      if (headType == 0x74) {
        var nameSize = BitConverter.ToUInt16(bytes, position + 26);
        var name = System.Text.Encoding.Latin1.GetString(bytes, position + 32, nameSize).Replace('\\', '/');
        if (string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase))
          return (position + headSize, (int)addSize, headFlags);
      }

      position += headSize + (int)addSize;
    }

    throw new AssertionException($"{entryName} not found in {archive}");
  }

  private static int FindEntry(RarReader reader, string entryName) {
    for (var i = 0; i < reader.Entries.Count; ++i) {
      var name = reader.Entries[i].Name;
      if (reader.Entries[i].IsDirectory)
        continue;

      if (entryName.StartsWith("*.", StringComparison.Ordinal)
            ? name.EndsWith(entryName[1..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase))
        return i;
    }

    throw new AssertionException($"Entry {entryName} not listed; entries: {string.Join(", ", reader.Entries.Select(e => e.Name))}");
  }

  private static byte[] ReadResource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RarLegacyVectors." + name)
                       ?? throw new AssertionException($"Missing embedded resource {name}");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static string ReadResourceText(string name) => System.Text.Encoding.UTF8.GetString(ReadResource(name));
}
