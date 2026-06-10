#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Arj;

namespace Compression.Tests.Arj;

/// <summary>
/// Locks the byte-identity contract for <see cref="ArjModifier.AddFile"/>.
/// ARJ ends every archive with a 4-byte EOA marker (header id <c>0xEA60</c>
/// LE + basic-header-size <c>0x0000</c>). The modifier overwrites that
/// marker with the new entry's block (which also begins with header id
/// <c>0xEA60</c> + nonzero basic-header-size), then writes a fresh EOA at
/// the new end. Consequently:
/// <list type="bullet">
///   <item><description><c>[0, oldLength - 4)</c> must be bitwise identical
///     — the body of every prior entry stays untouched.</description></item>
///   <item><description>Bytes <c>[oldLength - 4, oldLength - 2)</c> stay
///     identical too (the header-id <c>0xEA60</c> is the same in the EOA
///     and in the new entry's block header).</description></item>
///   <item><description>Bytes <c>[oldLength - 2, oldLength)</c> change:
///     they were <c>00 00</c> (EOA basicSize=0) and become the new entry's
///     nonzero basic-header-size.</description></item>
/// </list>
/// Locking this pattern catches a future agent who silently changes Add to
/// rebuild-and-replace, which would scramble bytes throughout the prefix.
/// </summary>
[TestFixture]
public class ArjByteIdentityTests {

  [Test, Category("ByteIdentity")]
  public void AddFile_PrefixBeforeEoaMarker_IsBitwiseIdentical() {
    var seed = BuildSeedArjBytes();
    var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    ArjModifier.AddFile(ms, "added.bin", "byte-identity-payload"u8.ToArray());

    // The last 4 bytes of seed are the EOA marker; the modifier overwrites
    // them. Bytes [0, seed.Length - 4) must remain bitwise identical.
    var stableLen = seed.Length - 4;
    Assert.That(ms.Length, Is.GreaterThan(seed.Length),
      "Add must enlarge the stream — new entry block + new EOA marker.");
    var prefix = new byte[stableLen];
    ms.Position = 0;
    ms.ReadExactly(prefix);
    var expected = seed.AsSpan(0, stableLen).ToArray();
    Assert.That(prefix, Is.EqualTo(expected),
      "ARJ Add must preserve all bytes up to the old EOA marker " +
      "(no prior entry body, name or CRC is rewritten).");

    // And the 2-byte header-id at oldLength-4 should still be 0xEA60 LE.
    ms.Position = stableLen;
    var b0 = ms.ReadByte();
    var b1 = ms.ReadByte();
    Assert.That(b0, Is.EqualTo(0x60),
      "Byte at oldLength-4 must still be the LE-low byte of ARJ header id 0xEA60.");
    Assert.That(b1, Is.EqualTo(0xEA),
      "Byte at oldLength-3 must still be the LE-high byte of ARJ header id 0xEA60.");
  }

  [Test, Category("ByteIdentity")]
  public void AddFile_MultipleAppends_EachPrefixToOldEoa_IsBitwiseIdentical() {
    var ms = new MemoryStream();
    ms.Write(BuildSeedArjBytes());

    var snapshots = new List<byte[]>();
    snapshots.Add(ms.ToArray());

    ArjModifier.AddFile(ms, "a.bin", "alpha"u8.ToArray());
    snapshots.Add(ms.ToArray());

    ArjModifier.AddFile(ms, "b.bin", "bravo"u8.ToArray());
    snapshots.Add(ms.ToArray());

    for (var i = 1; i < snapshots.Count; ++i) {
      var prev = snapshots[i - 1];
      var curr = snapshots[i];
      var stableLen = prev.Length - 4;
      var prefix = curr.AsSpan(0, stableLen).ToArray();
      var expected = prev.AsSpan(0, stableLen).ToArray();
      Assert.That(prefix, Is.EqualTo(expected),
        $"Snapshot {i}'s first {stableLen} bytes must equal snapshot {i - 1}'s " +
        $"(pre-EOA prefix preserved).");
    }
  }

  [Test, Category("ByteIdentity")]
  public void AddFile_ViaDescriptor_PrefixIsBitwiseIdentical() {
    var seed = BuildSeedArjBytes();
    var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-descriptor"u8.ToArray());
      ((IArchiveModifiable)new ArjFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via.bin", false)]);
    } finally { File.Delete(tmp); }

    var stableLen = seed.Length - 4;
    var prefix = new byte[stableLen];
    ms.Position = 0;
    ms.ReadExactly(prefix);
    var expected = seed.AsSpan(0, stableLen).ToArray();
    Assert.That(prefix, Is.EqualTo(expected),
      "Descriptor.Add must route through ArjModifier and preserve [0, oldLength-4).");
  }

  [Test, Category("Descriptor")]
  public void Descriptor_AdvertisesInPlaceModify() {
    var d = new ArjFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  private static byte[] BuildSeedArjBytes() {
    var ms = new MemoryStream();
    var w = new ArjWriter(0); // method 0 = Store
    w.AddFile("seed.bin", "seed-content"u8.ToArray());
    w.WriteTo(ms);
    return ms.ToArray();
  }
}
