#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// The keys of the encrypted volumes an operation is working on, by image stream: an edit,
/// defragmentation or shrink runs through many readers and writers that each open the volume
/// again, and this is where they find its unlocked key instead of each asking for it.
/// </summary>
/// <remarks>
/// A volume whose master key is stored in the clear is unlocked without anything remembered;
/// one whose key is wrapped needs its passphrase remembered for the stream first
/// (<see cref="Remember"/>), otherwise opening it asks for the passphrase
/// (<see cref="UnauthorizedAccessException"/>). Extent moves the block mover made on an
/// encrypted volume wait here too until the metadata is re-published around them, because an
/// encrypted node is never patched in place: that would reuse its keystream.
/// </remarks>
internal static class BcacheFsKeys {
  private sealed class State {
    internal string? Passphrase;
    internal BcacheFsCrypt? Crypt;
    internal Dictionary<(Bpos Position, long Sector), long>? PendingMoves;
  }

  private static readonly ConditionalWeakTable<Stream, State> States = new();

  private sealed class Forget(Stream image) : IDisposable {
    public void Dispose() => States.Remove(image);
  }

  private sealed class Nothing : IDisposable {
    internal static readonly Nothing Instance = new();
    public void Dispose() { }
  }

  /// <summary>Remembers <paramref name="passphrase"/> for <paramref name="image"/> until disposed.</summary>
  internal static IDisposable Remember(Stream image, string? passphrase) {
    ArgumentNullException.ThrowIfNull(image);
    if (string.IsNullOrEmpty(passphrase)) return Nothing.Instance;
    States.AddOrUpdate(image, new State { Passphrase = passphrase });
    return new Forget(image);
  }

  /// <summary>The unlocked key of the volume on <paramref name="image"/>; null for a plain volume.</summary>
  /// <exception cref="UnauthorizedAccessException">The key is wrapped and no (right) passphrase is remembered.</exception>
  internal static BcacheFsCrypt? For(Stream image, BcacheFsSuperblockRecord superblock) {
    if (!BcacheFsCrypt.IsEncrypted(superblock)) return null;
    var state = States.GetOrCreateValue(image);
    return state.Crypt ??= BcacheFsCrypt.Unlock(superblock, state.Passphrase);
  }

  /// <summary>As above, reading the superblock first; the stream position is kept.</summary>
  internal static BcacheFsCrypt? For(Stream image) {
    var position = image.Position;
    try {
      return BcacheFsDeviceSuperblocks.Read(image).Current is { } superblock ? For(image, superblock) : null;
    } finally {
      image.Position = position;
    }
  }

  /// <summary>Records that the extent at <paramref name="position"/> pointing at <paramref name="sector"/> now lies at <paramref name="newSector"/>.</summary>
  internal static void RecordMove(Stream image, Bpos position, long sector, long newSector) {
    var state = States.GetOrCreateValue(image);
    (state.PendingMoves ??= [])[(position, sector)] = newSector;
  }

  internal static bool HasPendingMoves(Stream image)
    => States.TryGetValue(image, out var state) && state.PendingMoves is { Count: > 0 };

  /// <summary>The extent key as it is after the moves recorded for it; the key itself when none was.</summary>
  internal static Key ApplyPendingMoves(Stream image, Key key) {
    if (key.Type != KeyExtent || !States.TryGetValue(image, out var state) || state.PendingMoves is not { Count: > 0 } moves
        || !BcacheFsExtentCodec.TryParseEntries(key.Value, out var entries, out _))
      return key;
    var value = (byte[])key.Value.Clone();
    var at = 0;
    foreach (var entry in entries) {
      if (entry.KnownType == BcacheFsExtentEntryType.Pointer) {
        var word = BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(at));
        if (moves.TryGetValue((key.Position, PointerSector(word)), out var moved)) {
          var device = (byte)((word >> 48) & 0xFF);
          var generation = (byte)((word >> 56) & 0xFF);
          BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(at), ExtentPointer(moved, device, generation));
        }
      }
      at += entry.RawBytes.Length;
    }
    return key with { Value = value };
  }

  /// <summary>Forgets the recorded moves once the metadata naming them has been published.</summary>
  internal static void ClearPendingMoves(Stream image) {
    if (States.TryGetValue(image, out var state)) state.PendingMoves = null;
  }
}
