#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Reiser4;

/// <summary>
/// Replays the format40 journal ("wandered logs") into an in-memory overlay, the way the kernel
/// and <c>fsck.reiser4</c> do before they read the tree.
/// </summary>
/// <remarks>
/// <para>Block 19 (journal header) names the last committed transaction, block 20 (journal footer)
/// the last one whose blocks were written back to their home locations. Every transaction between
/// the two, oldest first, is replayed: its head block (<c>"TxMagic4"</c>) links to the previous
/// transaction and to a chain of wander records (<c>"LogMagc4"</c>), each listing
/// <c>(original, wandered)</c> block pairs; replay copies each wandered block over its original.
/// The layout is written down in <c>docs/REISER4-NATIVE-PROFILE.md</c> and was confirmed on a
/// volume captured between a kernel commit and its write-back.</para>
/// </remarks>
internal static class Reiser4Journal {

  internal const ulong HeaderBlock = 19;
  internal const ulong FooterBlock = 20;

  private static readonly byte[] TxMagic = "TxMagic4"u8.ToArray();
  private static readonly byte[] RecordMagic = "LogMagc4"u8.ToArray();

  /// <summary>Outcome of a replay: the replaced blocks and how many transactions they came from.</summary>
  internal sealed record Replay(IReadOnlyDictionary<ulong, byte[]> Blocks, int Transactions);

  /// <summary>
  /// Replays every committed but unflushed transaction. Returns an empty replay when the journal
  /// has nothing pending, and <see langword="null" /> with a reason when the chain does not parse.
  /// </summary>
  internal static Replay? Read(Func<ulong, byte[]> readRaw, ulong blockCount, out string? problem) {
    problem = null;
    var header = readRaw(HeaderBlock);
    var footer = readRaw(FooterBlock);
    var lastCommitted = BinaryPrimitives.ReadUInt64LittleEndian(header);
    var lastFlushed = BinaryPrimitives.ReadUInt64LittleEndian(footer);
    var blocks = new Dictionary<ulong, byte[]>();
    if (lastCommitted == 0 || lastCommitted == lastFlushed) return new(blocks, 0);

    var chain = new List<byte[]>();
    var seen = new HashSet<ulong>();
    for (var tx = lastCommitted; tx != lastFlushed;) {
      if (tx == 0 || tx >= blockCount || !seen.Add(tx) || chain.Count > 4096) {
        problem = "the journal's transaction chain does not reach the last flushed transaction";
        return null;
      }
      var head = readRaw(tx);
      if (!head.AsSpan(0, TxMagic.Length).SequenceEqual(TxMagic)) {
        problem = $"journal block {tx} is not a transaction head";
        return null;
      }
      chain.Add(head);
      tx = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(24));
    }

    byte[] Current(ulong block) => blocks.TryGetValue(block, out var replaced) ? replaced : readRaw(block);
    for (var i = chain.Count - 1; i >= 0; --i) {
      var head = chain[i];
      var total = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(16));
      var record = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(32));
      var pending = new List<(ulong Original, ulong Wandered)>();
      for (var serial = 1U; serial < total; ++serial) {
        if (record == 0 || record >= blockCount) {
          problem = "a wander record lies outside the volume";
          return null;
        }
        var log = Current(record);
        if (!log.AsSpan(0, RecordMagic.Length).SequenceEqual(RecordMagic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(log.AsSpan(20)) != serial) {
          problem = $"journal block {record} is not wander record {serial}";
          return null;
        }
        for (var at = 32; at + 16 <= log.Length; at += 16) {
          var original = BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(at));
          var wandered = BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(at + 8));
          if (original == 0) break;
          if (original >= blockCount || wandered == 0 || wandered >= blockCount) {
            problem = "a wander record names a block outside the volume";
            return null;
          }
          pending.Add((original, wandered));
        }
        record = BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(24));
      }
      // Read every wandered copy before writing any home location: a transaction is applied whole.
      var copies = pending.Select(pair => (pair.Original, Bytes: Current(pair.Wandered).ToArray())).ToList();
      foreach (var (original, bytes) in copies) blocks[original] = bytes;
    }
    return new(blocks, chain.Count);
  }
}
