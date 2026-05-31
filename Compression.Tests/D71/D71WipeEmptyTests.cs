using Compression.Registry;
using FileSystem.D71;

namespace Compression.Tests.D71;

/// <summary>
/// Unused-space wiping for D71 (Commodore 1571) images. The descriptor
/// implements <see cref="IWipeEmpty"/> so free sectors can be zeroed without
/// disturbing live file content.
/// <para>
/// Cluster-tip wiping is not applicable to the 1571 layout: files are stored as
/// a chain of 256-byte sectors carrying a 2-byte track/sector link header plus
/// 254 payload bytes, so the directory-entry size does not map onto a
/// contiguous cluster-aligned tail. The wiper therefore clears whole free
/// sectors only.
/// </para>
/// </summary>
[TestFixture]
public class D71WipeEmptyTests {

  [Test, Category("Wipe")]
  public void WipeUnusedSpace_RoundTripsFileAndZerosFreeSectors() {
    var payload = new byte[200]; // < 254 payload bytes of one sector
    Array.Fill(payload, (byte)0xAA);

    var w = new D71Writer();
    w.AddFile("PAYLOAD", payload);
    var disk = w.Build();

    using var ms = new MemoryStream();
    ms.Write(disk);

    // Dirty a known free sector so the wipe has observable work to do.
    var descriptor = new D71FormatDescriptor();
    ms.Position = 0;
    var freeExtent = descriptor.EnumerateExtents(ms)
                               .First(e => e.Kind == DefragBlockKind.Free && e.Length >= 256);
    var dirtyOffset = freeExtent.Offset;
    ms.Position = dirtyOffset;
    var dirty = new byte[256];
    Array.Fill(dirty, (byte)0xFF);
    ms.Write(dirty);

    descriptor.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    // Free sector is zeroed.
    var bytes = ms.ToArray();
    for (var i = dirtyOffset; i < dirtyOffset + 256; i++)
      Assert.That(bytes[i], Is.EqualTo(0), $"free sector byte at {i} must be zero after wipe");

    // File still round-trips intact.
    ms.Position = 0;
    var reader = new D71Reader(ms);
    var entry = reader.Entries.Single();
    Assert.That(entry.Name, Is.EqualTo("PAYLOAD"));
    Assert.That(reader.Extract(entry), Is.EqualTo(payload), "file content intact after wipe");
  }
}
