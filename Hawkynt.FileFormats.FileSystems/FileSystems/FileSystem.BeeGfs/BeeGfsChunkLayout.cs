#pragma warning disable CS1591

namespace FileSystem.BeeGfs;

/// <summary>
/// BeeGFS chunk-path and RAID0 offset mapping derived from the published/upstream
/// storage layout rules. This is kept separate from metadata decoding so path and
/// stripe arithmetic can be tested independently.
/// </summary>
internal static class BeeGfsChunkLayout {
  private const int TimestampDayReversePosition = 3;

  public static string BuildChunkRelativePath(
      uint originalUid,
      string originalParentEntryId,
      string entryId) {
    ArgumentException.ThrowIfNullOrWhiteSpace(originalParentEntryId);
    ArgumentException.ThrowIfNullOrWhiteSpace(entryId);

    var timestamp = TimestampFromEntryId(originalParentEntryId);
    var last = timestamp.Length - 1;
    var dayPosition = last - TimestampDayReversePosition;
    var day = dayPosition < 0 ? "0" : timestamp.Substring(dayPosition, 1);
    var yearMonth = dayPosition <= 0 ? "0" : timestamp[..dayPosition];
    return $"u{originalUid:X}/{yearMonth}/{day}/{originalParentEntryId}/{entryId}";
  }

  public static int TargetIndex(long fileOffset, uint chunkSize, int targetCount) {
    ValidateStripe(fileOffset, chunkSize, targetCount);
    return checked((int)((fileOffset / chunkSize) % targetCount));
  }

  public static long TargetLocalOffset(long fileOffset, uint chunkSize, int targetCount) {
    ValidateStripe(fileOffset, chunkSize, targetCount);
    var chunkNumber = fileOffset / chunkSize;
    var stripeSet = chunkNumber / targetCount;
    var withinChunk = fileOffset % chunkSize;
    return checked(stripeSet * chunkSize + withinChunk);
  }

  public static int BytesUntilNextChunk(long fileOffset, uint chunkSize, int maximum) {
    if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
    ValidateStripe(fileOffset, chunkSize, targetCount: 1);
    var withinChunk = fileOffset % chunkSize;
    var remaining = checked((long)chunkSize - withinChunk);
    return checked((int)Math.Min(remaining, maximum));
  }

  private static string TimestampFromEntryId(string entryId) {
    var first = entryId.IndexOf('-');
    if (first < 0) return entryId;
    var second = entryId.IndexOf('-', first + 1);
    return second < 0 ? entryId : entryId[(first + 1)..second];
  }

  private static void ValidateStripe(long fileOffset, uint chunkSize, int targetCount) {
    if (fileOffset < 0) throw new ArgumentOutOfRangeException(nameof(fileOffset));
    if (chunkSize < 64 * 1024 || (chunkSize & (chunkSize - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(chunkSize), "BeeGFS chunk size must be a power of two and at least 64 KiB.");
    if (targetCount <= 0) throw new ArgumentOutOfRangeException(nameof(targetCount));
  }
}
