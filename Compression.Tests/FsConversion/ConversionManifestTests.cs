#pragma warning disable CS1591
using Compression.Lib.FsConversion;

namespace Compression.Tests.FsConversion;

/// <summary>
/// Unit tests for the binary serialization + CRC-protected parsing in
/// <see cref="ConversionManifest"/>. These guarantee that the recovery paths
/// in <see cref="MigrationConverter"/> can reliably distinguish a valid
/// manifest from a torn or corrupted one.
/// </summary>
[TestFixture]
public class ConversionManifestTests {

  [Test, Category("ConversionManifest")]
  public void RoundTrip_PreservesAllEntries() {
    var original = new ConversionManifest();
    original.Entries.Add(new ConversionManifestEntry { SourcePath = "a.txt", Status = ConversionEntryStatus.Pending, Size = 10 });
    original.Entries.Add(new ConversionManifestEntry { SourcePath = "b.bin", Status = ConversionEntryStatus.Copying, Size = 1024 });
    original.Entries.Add(new ConversionManifestEntry { SourcePath = "c/d/e.dat", Status = ConversionEntryStatus.Done, Size = 0 });

    var bytes = original.Serialize();
    var parsed = ConversionManifest.TryParse(bytes);
    Assert.That(parsed, Is.Not.Null);
    Assert.That(parsed!.Entries.Count, Is.EqualTo(3));
    Assert.That(parsed.Entries[0].SourcePath, Is.EqualTo("a.txt"));
    Assert.That(parsed.Entries[0].Status, Is.EqualTo(ConversionEntryStatus.Pending));
    Assert.That(parsed.Entries[0].Size, Is.EqualTo(10));
    Assert.That(parsed.Entries[1].Status, Is.EqualTo(ConversionEntryStatus.Copying));
    Assert.That(parsed.Entries[2].SourcePath, Is.EqualTo("c/d/e.dat"));
    Assert.That(parsed.Entries[2].Status, Is.EqualTo(ConversionEntryStatus.Done));
  }

  [Test, Category("ConversionManifest")]
  public void Empty_Roundtrips() {
    var bytes = new ConversionManifest().Serialize();
    var parsed = ConversionManifest.TryParse(bytes);
    Assert.That(parsed, Is.Not.Null);
    Assert.That(parsed!.Entries, Is.Empty);
  }

  [Test, Category("ConversionManifest")]
  public void TryParse_TornBlob_ReturnsNull() {
    var original = new ConversionManifest();
    original.Entries.Add(new ConversionManifestEntry { SourcePath = "x.dat", Status = ConversionEntryStatus.Pending, Size = 999 });
    var bytes = original.Serialize();

    // Truncate inside the entry payload — CRC won't match.
    var torn = new byte[bytes.Length / 2];
    Array.Copy(bytes, torn, torn.Length);
    Assert.That(ConversionManifest.TryParse(torn), Is.Null);
  }

  [Test, Category("ConversionManifest")]
  public void TryParse_CorruptedBody_ReturnsNull() {
    var original = new ConversionManifest();
    original.Entries.Add(new ConversionManifestEntry { SourcePath = "x.dat", Status = ConversionEntryStatus.Pending, Size = 999 });
    var bytes = original.Serialize();

    // Flip a byte inside the body (before the trailing CRC).
    bytes[bytes.Length / 2] ^= 0xFF;
    Assert.That(ConversionManifest.TryParse(bytes), Is.Null,
      "Body corruption must be detected by the trailing CRC.");
  }

  [Test, Category("ConversionManifest")]
  public void TryParse_BadMagic_ReturnsNull() {
    var bytes = new ConversionManifest().Serialize();
    bytes[0] = (byte)'X';
    Assert.That(ConversionManifest.TryParse(bytes), Is.Null);
  }

  [Test, Category("ConversionManifest")]
  public void TryParse_NullOrTooSmall_ReturnsNull() {
    Assert.That(ConversionManifest.TryParse(null!), Is.Null);
    Assert.That(ConversionManifest.TryParse([]), Is.Null);
    Assert.That(ConversionManifest.TryParse(new byte[5]), Is.Null);
  }

  [Test, Category("ConversionManifest")]
  public void TryParse_UnknownVersion_ReturnsNull() {
    var manifest = new ConversionManifest();
    var bytes = manifest.Serialize();
    // Version field is at offset 8 (after the 8-byte magic). Bump it.
    bytes[8] = 99;
    // CRC will now mismatch, so the parse rejects it. Both paths exist as a
    // defence-in-depth check.
    Assert.That(ConversionManifest.TryParse(bytes), Is.Null);
  }

  [Test, Category("ConversionManifest")]
  public void FileName_HasLeadingDot() {
    // Sanity check: the on-disk name uses a leading dot so it's
    // conventionally hidden on Unix and easily filtered by callers.
    Assert.That(ConversionManifest.FileName, Does.StartWith("."));
  }
}
