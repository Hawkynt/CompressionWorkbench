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

  private static int LocatePackedData(byte[] bytes, string archive, string entryName) {
    // Walk the RAR 4 block chain to the entry's header; its packed data follows the header.
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
          return position + headSize + (int)(addSize / 2);
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
