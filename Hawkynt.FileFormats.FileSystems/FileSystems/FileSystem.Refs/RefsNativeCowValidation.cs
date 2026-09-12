#pragma warning disable CS1591

namespace FileSystem.Refs;

internal static class RefsNativeCowValidation {
  public static void ValidateRedoTransaction(IReadOnlyList<RefsRedoRecord> records) {
    ArgumentNullException.ThrowIfNull(records);
    if (records.Count == 0)
      throw new ArgumentException("A native ReFS publication requires at least one redo record.", nameof(records));
    if ((records[0].Flags & RefsRedoFlags.TransactionStart) == 0)
      throw new InvalidDataException("The first ReFS redo record must carry TransactionStart.");

    for (var i = 0; i < records.Count; ++i) {
      var record = records[i];
      if (!Enum.IsDefined(record.Opcode) || record.Opcode == RefsRedoOpcode.ReservedUnhandled)
        throw new NotSupportedException($"ReFS redo opcode 0x{(uint)record.Opcode:X2} is not publishable.");
      if (i > 0 && (record.Flags & RefsRedoFlags.TransactionStart) != 0)
        throw new InvalidDataException("A single ReFS MLog block cannot contain multiple TransactionStart records.");
      if ((record.Flags & RefsRedoFlags.CommitMarker) != 0)
        throw new InvalidDataException(
          "Observed ReFS 3.x redo records do not use the per-record commit bit; commit belongs to MLog/CHKP publication.");
    }

    // Full serialization is part of preflight so size/overflow errors happen
    // before allocator roots, MLog control state, or the alternate checkpoint
    // are touched.
    _ = RefsRedoCodec.SerializeBlock(records);
  }
}
