#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Lzh;

namespace Compression.Tests.Lzh;

/// <summary>
/// Locks the byte-identity contract for <see cref="LhaModifier.AddFile"/>:
/// LHA has no central directory and no trailing end-of-archive marker, so a
/// fresh Stored/-lh5- entry written at the chain's tail position is a pure
/// append. The prefix <c>[0, oldLength)</c> must remain bitwise identical
/// after Add. This pins the contract so a future agent who changes the
/// writer to emit a trailing zero-terminator (which would technically be
/// legal LHA) gets a loud failure rather than silently demoting the format
/// to a rebuild-disguised modifier.
/// </summary>
[TestFixture]
public class LhaByteIdentityTests {

  [Test, Category("ByteIdentity")]
  public void AddFile_PrefixIsBitwiseIdenticalToOriginal() {
    var seed = BuildSeedLhaBytes();
    var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    LhaModifier.AddFile(ms, "added.txt", "byte-identity-payload"u8.ToArray());

    Assert.That(ms.Length, Is.GreaterThan(seed.Length),
      "Add must enlarge the stream — pure append semantics.");
    var prefix = new byte[seed.Length];
    ms.Position = 0;
    ms.ReadExactly(prefix);
    Assert.That(prefix, Is.EqualTo(seed),
      "LHA Add must preserve [0, oldLength) byte-for-byte (no header is " +
      "patched, no terminator is rewritten).");
  }

  [Test, Category("ByteIdentity")]
  public void AddFile_MultipleAppends_EachPrefixIsBitwiseIdentical() {
    var ms = new MemoryStream();
    ms.Write(BuildSeedLhaBytes());

    var snapshots = new List<byte[]>();
    snapshots.Add(ms.ToArray());

    LhaModifier.AddFile(ms, "a.txt", "alpha"u8.ToArray());
    snapshots.Add(ms.ToArray());

    LhaModifier.AddFile(ms, "b.txt", "bravo"u8.ToArray());
    snapshots.Add(ms.ToArray());

    LhaModifier.AddFile(ms, "c.txt", "charlie"u8.ToArray());
    snapshots.Add(ms.ToArray());

    // Each successive snapshot starts with the previous one byte-identical.
    for (var i = 1; i < snapshots.Count; ++i) {
      var prev = snapshots[i - 1];
      var curr = snapshots[i];
      Assert.That(curr.Length, Is.GreaterThan(prev.Length),
        $"Snapshot {i} must be longer than snapshot {i - 1}.");
      var prefix = curr.AsSpan(0, prev.Length).ToArray();
      Assert.That(prefix, Is.EqualTo(prev),
        $"Snapshot {i} prefix must equal snapshot {i - 1} (pure append).");
    }
  }

  [Test, Category("ByteIdentity")]
  public void AddFile_ViaDescriptor_PrefixIsBitwiseIdentical() {
    var seed = BuildSeedLhaBytes();
    var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-descriptor"u8.ToArray());
      ((IArchiveModifiable)new LzhFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via.txt", false)]);
    } finally { File.Delete(tmp); }

    var prefix = new byte[seed.Length];
    ms.Position = 0;
    ms.ReadExactly(prefix);
    Assert.That(prefix, Is.EqualTo(seed),
      "Descriptor.Add must route through LhaModifier and preserve [0, oldLength).");
  }

  /// <summary>
  /// Descriptor claims <see cref="FormatCapabilities.CanModify"/> and
  /// implements <see cref="IArchiveModifiable"/> — locked here so a future
  /// agent demoting the modifier without dropping the flag gets caught.
  /// </summary>
  [Test, Category("Descriptor")]
  public void Descriptor_AdvertisesInPlaceModify() {
    var d = new LzhFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  private static byte[] BuildSeedLhaBytes() {
    var ms = new MemoryStream();
    var w = new LhaWriter(LhaConstants.MethodLh5);
    w.AddFile("seed.txt", "seed-content"u8.ToArray());
    w.WriteTo(ms);
    return ms.ToArray();
  }
}
