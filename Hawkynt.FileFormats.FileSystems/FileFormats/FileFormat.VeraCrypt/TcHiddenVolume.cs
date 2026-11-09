using System.Security.Cryptography;
using System.Text;
using Compression.Registry;

namespace FileFormat.VeraCrypt;

/// <summary>
/// Hidden volumes: a second volume inside the free space at the end of an outer volume, whose
/// header sits in the outer container's spare header slot (64 KiB, backup 64 KiB before the end)
/// and is indistinguishable from random bytes without its password. Placement is VeraCrypt's: the
/// hidden data ends 4 KiB before the backup header area.
/// </summary>
public static class TcHiddenVolume {
  /// <summary>Bytes VeraCrypt keeps free between the hidden data and the backup header area.</summary>
  public const long ReservedEnd = 4096;

  /// <summary>Byte offset in the container where a hidden volume of <paramref name="size"/> bytes starts.</summary>
  public static long DataOffset(long containerLength, long size) => containerLength - TcVolume.BackupHeaderFromEnd - ReservedEnd - size;

  /// <summary>
  /// Writes a hidden volume holding <paramref name="payload"/> into <paramref name="container"/>,
  /// an existing TrueCrypt 5+/VeraCrypt container. Refuses unless the outer volume, opened with
  /// <paramref name="outerPassword"/>, proves the space free: its filesystem must map its
  /// allocations, and none may reach into the hidden area.
  /// </summary>
  /// <exception cref="ArchivePasswordException">The outer password does not open the container.</exception>
  /// <exception cref="InvalidOperationException">The outer volume cannot prove the hidden area free.</exception>
  public static void Add(Stream container, string outerPassword, TcUnlockOptions? outerOptions, ReadOnlySpan<byte> payload,
      string hiddenPassword, TcCreateOptions hiddenOptions, long size) {
    ArgumentNullException.ThrowIfNull(container);
    var outer = TcVolume.Open(container, outerPassword, outerOptions)
      ?? throw new ArchivePasswordException("VeraCrypt/TrueCrypt: the outer password does not open this container.", passwordWasSupplied: true);
    if (ProvenFreeFrom(outer, container) is not { } freeFrom)
      throw new InvalidOperationException("The outer volume's filesystem cannot prove which of its space is free (no recognised filesystem with an allocation map); refusing to place a hidden volume where it might overwrite outer data.");
    Write(container, outer, freeFrom, payload, hiddenPassword, hiddenOptions, size);
  }

  /// <summary>Writes the hidden volume once the outer volume's free space from <paramref name="freeFrom"/> (relative to its data area) is known.</summary>
  internal static void Write(Stream container, TcVolume outer, long freeFrom, ReadOnlySpan<byte> payload, string hiddenPassword, TcCreateOptions options, long size) {
    if (!container.CanSeek || !container.CanWrite)
      throw new ArgumentException("Adding a hidden volume needs a seekable, writable container.", nameof(container));
    if (options.Sparse)
      throw new ArgumentException("A hidden volume cannot be sparse: its unused space must look like the outer volume's.", nameof(options));
    if (outer.Kind != TcHeaderKind.Standard || outer.Version < 4 || outer.DataOffset != TcVolume.DataAreaOffset)
      throw new NotSupportedException("Hidden volumes can only be added to TrueCrypt 6+/VeraCrypt containers.");
    if (size <= 0 || size % 512 != 0 || payload.Length % 512 != 0 || payload.Length > size)
      throw new ArgumentException("The hidden volume size must be whole 512-byte sectors and hold the payload.", nameof(size));
    var offset = DataOffset(container.Length, size);
    if (offset - outer.DataOffset < freeFrom)
      throw new InvalidOperationException($"A {size}-byte hidden volume would start at outer byte {offset - outer.DataOffset}, but the outer volume uses space up to {freeFrom}.");

    var chain = options.ResolveChain();
    var kdf = options.ResolveKdf();
    var pw = Encoding.UTF8.GetBytes(hiddenPassword);
    var keys = RandomNumberGenerator.GetBytes(256);
    try {
      var header = TcVolume.BuildHeader(options.VeraCrypt, kdf, options.Pim, chain, pw, options.Keyfiles, keys,
        (ulong)size, (ulong)size, (ulong)offset, (ulong)size);
      var backup = TcVolume.BuildHeader(options.VeraCrypt, kdf, options.Pim, chain, pw, options.Keyfiles, keys,
        (ulong)size, (ulong)size, (ulong)offset, (ulong)size);

      var data = new byte[size];
      payload.CopyTo(data);
      RandomNumberGenerator.Fill(data.AsSpan(payload.Length));  // unused hidden space: indistinguishable either way
      using (var cipher = new TcSectorCipher(chain, keys, offset / 512))
        cipher.Encrypt(data.AsSpan(0, payload.Length), 0);
      container.Position = offset;
      container.Write(data);
      CryptographicOperations.ZeroMemory(data);

      container.Position = TcVolume.HiddenHeaderOffset;
      container.Write(header);
      container.Position = container.Length - TcVolume.HiddenBackupFromEnd;
      container.Write(backup);
      container.Flush();
    } finally {
      CryptographicOperations.ZeroMemory(keys);
      CryptographicOperations.ZeroMemory(pw);
    }
  }

  /// <summary>
  /// The offset in <paramref name="outer"/>'s data area from which its filesystem proves every byte
  /// free, or <see langword="null"/> when no recognised filesystem maps its allocations.
  /// </summary>
  public static long? ProvenFreeFrom(TcVolume outer, Stream container) {
    using var device = outer.OpenBlockDevice(container, writable: false);
    using var plain = new BlockDeviceStream(device, leaveOpen: true);
    if (InnerFsDetector.Detect(plain) is not IFilesystemExtentMap map)
      return null;
    plain.Position = 0;
    long end = 0;
    foreach (var extent in map.EnumerateExtents(plain))
      if (extent.Kind != DefragBlockKind.Free)
        end = Math.Max(end, extent.Offset + extent.Length);
    return end;
  }
}

/// <summary>
/// The outer volume with hidden-volume protection: any write that would reach the hidden volume is
/// refused, and from then on every write is refused (as VeraCrypt does), so the outer filesystem
/// cannot go on half-written over it.
/// </summary>
public sealed class HiddenProtectedBlockDevice(IRandomAccessBlockDevice outer, long protectedFirstBlock) : IRandomAccessBlockDevice {
  /// <summary>Whether a write was refused; the volume is read-only from then on.</summary>
  public bool ProtectionTriggered { get; private set; }

  public BlockDeviceGeometry Geometry => outer.Geometry;
  public bool CanWrite => outer.CanWrite;
  public int ReadBlocks(long firstBlock, Span<byte> destination) => outer.ReadBlocks(firstBlock, destination);

  public void WriteBlocks(long firstBlock, ReadOnlySpan<byte> source) {
    if (this.ProtectionTriggered)
      throw new IOException("Hidden-volume protection was triggered earlier; the outer volume refuses all writes until it is reopened.");
    var count = source.Length / outer.Geometry.LogicalBlockSize;
    if (firstBlock + count > protectedFirstBlock) {
      this.ProtectionTriggered = true;
      throw new IOException($"Hidden-volume protection: writing blocks {firstBlock}–{firstBlock + count - 1} would overwrite the hidden volume (from block {protectedFirstBlock}); the outer volume is now read-only.");
    }
    outer.WriteBlocks(firstBlock, source);
  }

  public void Trim(long firstBlock, long blockCount) => outer.Trim(firstBlock, blockCount);
  public void Flush() => outer.Flush();
  public void Dispose() => outer.Dispose();
}
