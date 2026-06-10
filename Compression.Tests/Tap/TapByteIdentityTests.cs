using Compression.Registry;
using FileFormat.Tap;

namespace Compression.Tests.Tap;

/// <summary>
/// Byte-identity contract tests for ZX Spectrum TAP tape images.
///
/// <para>TAP is a chained-blocks format with no global header: every entry
/// is a header-block + data-block pair, each preceded by a uint16 LE length.
/// TapModifier.AddFile appends header+data at EOF without touching any
/// prior bytes, so the [0, oldLength) prefix is byte-identical after Add.
/// This fixture locks that contract — a future change to TapModifier can't
/// silently corrupt earlier blocks.</para>
/// </summary>
[TestFixture]
public class TapByteIdentityTests {

  [Test, Category("ContractLock")]
  public void TapModifier_AddFile_PrefixUnchanged_ByteIdentical() {
    // Build a TAP with two files, snapshot the bytes, then append a third.
    using var ms = new MemoryStream();
    using (var w = new TapWriter(ms, leaveOpen: true)) {
      w.AddFile("first",  "alpha"u8.ToArray());
      w.AddFile("second", "beta payload"u8.ToArray());
    }
    var prefix = ms.ToArray();
    var prefixLength = prefix.Length;

    // Append a new file at EOF via the modifier.
    TapModifier.AddFile(ms, "third", "gamma!"u8.ToArray());

    // Re-read entire stream and verify the first prefixLength bytes are
    // byte-identical to the snapshot.
    ms.Position = 0;
    var after = ms.ToArray();
    Assert.That(after.Length, Is.GreaterThan(prefixLength),
      "Add must extend the stream (appended at EOF)");
    Assert.That(after.AsSpan(0, prefixLength).SequenceEqual(prefix), Is.True,
      "Bytes [0, oldLength) must be byte-identical after TapModifier.AddFile " +
      "(append-only contract)");
  }

  [Test, Category("ContractLock")]
  public void TapDescriptor_Add_PrefixUnchanged_ByteIdentical() {
    // Same contract via the IArchiveModifiable descriptor entry-point.
    using var ms = new MemoryStream();
    using (var w = new TapWriter(ms, leaveOpen: true)) {
      w.AddFile("orig", "original data"u8.ToArray());
    }
    var prefix = ms.ToArray();
    var prefixLength = prefix.Length;

    var desc = new TapFormatDescriptor();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "appended"u8.ToArray());
      ((IArchiveModifiable)desc).Add(ms, [new ArchiveInputInfo(tmp, "added", false)]);
    } finally {
      File.Delete(tmp);
    }

    ms.Position = 0;
    var after = ms.ToArray();
    Assert.That(after.AsSpan(0, prefixLength).SequenceEqual(prefix), Is.True,
      "Descriptor.Add must preserve [0, oldLength) byte-identical " +
      "(delegates to TapModifier.AddFile)");
  }

  [Test, Category("ContractLock")]
  public void TapModifier_AddFile_OnEmptyStream_StartsAtOffsetZero() {
    using var ms = new MemoryStream();
    TapModifier.AddFile(ms, "first", "hello"u8.ToArray());

    Assert.That(ms.Length, Is.GreaterThan(0),
      "Empty-stream Add must produce a complete header+data pair");

    ms.Position = 0;
    var r = new TapReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("first"));
  }

  [Test, Category("ContractLock")]
  public void TapModifier_AddFile_AppendsAtExactEofOffset() {
    using var ms = new MemoryStream();
    using (var w = new TapWriter(ms, leaveOpen: true)) {
      w.AddFile("anchor", new byte[] { 0xAA, 0xBB, 0xCC });
    }
    var oldEof = ms.Length;

    TapModifier.AddFile(ms, "appended", new byte[] { 0xDD, 0xEE });

    // First 2 bytes after oldEof must be the uint16 LE block length of the
    // 19-byte header block ("19" little-endian = 0x13 0x00).
    ms.Position = oldEof;
    Assert.That(ms.ReadByte(), Is.EqualTo(0x13),
      "First byte after oldEof must be header-block length low byte (19)");
    Assert.That(ms.ReadByte(), Is.EqualTo(0x00),
      "Second byte after oldEof must be header-block length high byte");
  }
}
