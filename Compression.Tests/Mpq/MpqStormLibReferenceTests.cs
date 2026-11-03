using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Mpq;

namespace Compression.Tests.Mpq;

/// <summary>
/// Archives written by StormLib (the maintained MPQ library) extract here to exactly the bytes
/// StormLib extracts: every compression mask, imploded and encrypted files, the adjusted key,
/// single-unit files, sector checksums, and version 2 headers with LZMA and sparse.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
public class MpqStormLibReferenceTests {

  internal static byte[] Resource(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MpqVectors." + name)
      ?? throw new InvalidOperationException($"Missing embedded vector {name}.");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  /// <summary>Rows of a manifest: archived name, size, flags (hex), SHA-256 of StormLib's extraction.</summary>
  internal static IEnumerable<(string Name, long Size, uint Flags, string Sha256)> Manifest(string name)
    => System.Text.Encoding.UTF8.GetString(Resource(name))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(line => line.TrimEnd('\r').Split('\t'))
      .Where(f => f.Length == 4)
      .Select(f => (f[0], long.Parse(f[1]), Convert.ToUInt32(f[2], 16), f[3]));

  [TestCase("stormlib-v1")]
  [TestCase("stormlib-v2")]
  public void Given_AStormLibArchive_When_EveryFileIsExtracted_Then_TheBytesMatchStormLibs(string archive) {
    using var stream = new MemoryStream(Resource(archive + ".mpq"));
    var reader = new MpqReader(stream);
    var byName = reader.Entries.ToDictionary(e => e.FileName, StringComparer.OrdinalIgnoreCase);

    var rows = Manifest(archive + ".manifest").ToList();
    Assert.That(rows, Is.Not.Empty);
    foreach (var (name, size, flags, sha256) in rows) {
      Assert.That(byName, Does.ContainKey(name), name);
      var entry = byName[name];
      Assert.That(entry.Flags, Is.EqualTo(flags), name);
      Assert.That(entry.OriginalSize, Is.EqualTo(size), name);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.Extract(entry))), Is.EqualTo(sha256), name);
    }
  }

  [Test]
  public void Given_TheVersion2Archive_When_Read_Then_ItIsVersion2() {
    using var stream = new MemoryStream(Resource("stormlib-v2.mpq"));
    Assert.That(new MpqReader(stream).FormatVersion, Is.EqualTo(1));
  }

  [Test]
  public void Given_EncryptedFilesWithoutAListfile_When_Extracted_Then_TheKeyIsRecoveredFromTheSectorTable() {
    var archive = Resource("stormlib-v1.mpq");
    var named = ReadAll(archive);
    var withoutNames = HideListfile(archive);

    using var stream = new MemoryStream(withoutNames);
    var reader = new MpqReader(stream);
    Assert.That(reader.Entries.Any(e => e.IsNameKnown && e.FileName == "war3map.j"), Is.False);
    // war3map.j: PKWARE, encrypted with the adjusted key, many sectors -> recoverable by its sector table.
    var expected = named["war3map.j"];
    var match = reader.Entries.Where(e => !e.IsNameKnown && e.IsEncrypted && !e.IsSingleUnit && e.OriginalSize == expected.Length)
      .Select(e => reader.Extract(e))
      .Any(bytes => bytes.AsSpan().SequenceEqual(expected));
    Assert.That(match, Is.True);
  }

  [TestCase("Scripts\\implode.txt", Description = "imploded + encrypted, caught by the (attributes) CRC-32")]
  [TestCase("single.txt", Description = "zlib single unit, caught by zlib's Adler-32")]
  public void Given_ACorruptedFile_When_Extracted_Then_ItIsRefused(string name) {
    var archive = Resource("stormlib-v1.mpq");
    using var probe = new MemoryStream(archive);
    var reader = new MpqReader(probe);
    var entry = reader.Entries.Single(e => e.FileName == name);
    archive[(int)(reader.HeaderOffset + entry.FileOffset + 64)] ^= 0x5A;
    using var damaged = new MemoryStream(archive);
    var damagedReader = new MpqReader(damaged);
    Assert.Throws<InvalidDataException>(() => damagedReader.Extract(damagedReader.Entries.Single(e => e.FileName == name)));
  }

  [Test]
  public void Given_ASectorChecksumMismatch_When_Extracted_Then_ItIsRefused() {
    var archive = Resource("stormlib-v2.mpq");
    using var probe = new MemoryStream(archive);
    var reader = new MpqReader(probe);
    var entry = reader.Entries.Single(e => e.FileName == "records.bin");
    Assert.That(entry.HasSectorChecksums, Is.True);
    archive[(int)(reader.HeaderOffset + entry.FileOffset + 40)] ^= 0x01;
    using var damaged = new MemoryStream(archive);
    var damagedReader = new MpqReader(damaged);
    Assert.Throws<InvalidDataException>(() => damagedReader.Extract(damagedReader.Entries.Single(e => e.FileName == "records.bin")));
  }

  [Test]
  public void Given_AnArchiveBehindAStub_When_Read_Then_TheHeaderIsFoundOnA512ByteBoundary() {
    var archive = Resource("stormlib-v2.mpq");
    var shifted = new byte[0x400 + archive.Length];
    "HM3W"u8.CopyTo(shifted);
    archive.CopyTo(shifted, 0x400);
    using var stream = new MemoryStream(shifted);
    var reader = new MpqReader(stream);
    Assert.That(reader.HeaderOffset, Is.EqualTo(0x400));
    var text = reader.Extract(reader.Entries.Single(e => e.FileName == "lzma.txt"));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(text)),
      Is.EqualTo(Manifest("stormlib-v2.manifest").Single(r => r.Name == "lzma.txt").Sha256));
  }

  internal static Dictionary<string, byte[]> ReadAll(byte[] archive) {
    using var stream = new MemoryStream(archive);
    var reader = new MpqReader(stream);
    return reader.Entries.Where(e => e.IsNameKnown).ToDictionary(e => e.FileName, reader.Extract, StringComparer.OrdinalIgnoreCase);
  }

  /// <summary>Renames the (listfile) hash entry so nothing names the other files.</summary>
  private static byte[] HideListfile(byte[] archive) {
    var copy = (byte[])archive.Clone();
    using var stream = new MemoryStream(copy);
    var reader = new MpqReader(stream);
    var header = (int)reader.HeaderOffset;
    var hashOffset = BitConverter.ToInt32(copy, header + 16);
    var hashCount = BitConverter.ToInt32(copy, header + 24);
    var table = copy.AsSpan(header + hashOffset, hashCount * 16).ToArray();
    MpqCryptoAccess.Decrypt(table, 0xC3AF3770);
    var listA = MpqCryptoAccess.Hash("(listfile)", 0x100);
    for (var i = 0; i < hashCount; ++i)
      if (BitConverter.ToUInt32(table, i * 16) == listA)
        BitConverter.GetBytes(0x12345678u).CopyTo(table, i * 16);
    MpqCryptoAccess.Encrypt(table, 0xC3AF3770);
    table.CopyTo(copy, header + hashOffset);
    return copy;
  }
}

/// <summary>Test access to the internal MPQ crypto helpers.</summary>
internal static class MpqCryptoAccess {
  public static void Decrypt(byte[] data, uint key) => MpqCrypto.DecryptBlock(data, key);
  public static void Encrypt(byte[] data, uint key) => MpqCrypto.EncryptBlock(data, key);
  public static uint Hash(string name, uint type) => MpqCrypto.HashString(name, type);
}
