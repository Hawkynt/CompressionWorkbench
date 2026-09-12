#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FileSystem.JuiceFs;

internal static class JuiceFsJsonBackup {
  public static bool TryParse(byte[] data, List<JuiceFsEntry> entries) {
    if (data.Length == 0)
      return false;
    var first = FirstNonWhitespace(data);
    if (first < 0 || data[first] != (byte)'{')
      return false;

    try {
      using var document = JsonDocument.Parse(data, new JsonDocumentOptions {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = int.MaxValue,
      });
      var root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object
          || !root.TryGetProperty("Setting", out var setting) || setting.ValueKind != JsonValueKind.Object
          || !root.TryGetProperty("Counters", out var counters) || counters.ValueKind != JsonValueKind.Object
          || !root.TryGetProperty("FSTree", out var fsTree) || fsTree.ValueKind != JsonValueKind.Object)
        return false;

      var manifest = BuildManifest(fsTree);
      AddGenerated(entries, "metadata.ini", BuildMetadata(setting, counters, manifest));
      AddGenerated(entries, "manifest.tsv", manifest.Data);
      AddSource(entries, "juicefs-dump.json", 0, data.LongLength);
      return true;
    } catch (Exception ex) when (ex is JsonException or InvalidOperationException) {
      entries.Clear();
      return false;
    }
  }

  private static int FirstNonWhitespace(ReadOnlySpan<byte> data) {
    for (var i = 0; i < data.Length; ++i) {
      var b = data[i];
      if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n')
        return i;
    }
    return -1;
  }

  private static ManifestInfo BuildManifest(JsonElement fsTree) {
    var builder = new StringBuilder("dump_path\ttype\tinode\tlength\tchunks\tslices\n");
    var stack = new Stack<(JsonElement Entry, string Path)>();
    stack.Push((fsTree, "/"));
    var entryCount = 0;
    var directoryCount = 0;
    var fileCount = 0;
    var chunkCount = 0;
    var sliceCount = 0;
    ulong logicalBytes = 0;

    while (stack.TryPop(out var current)) {
      var entry = current.Entry;
      if (entry.ValueKind != JsonValueKind.Object)
        throw new JsonException("JuiceFS FSTree entries must be JSON objects.");
      var attr = entry.TryGetProperty("attr", out var attrValue) && attrValue.ValueKind == JsonValueKind.Object
        ? attrValue
        : default;
      var type = ReadString(attr, "type") ?? "unknown";
      var inode = ReadUInt64(attr, "inode");
      var length = ReadUInt64(attr, "length");
      var chunks = 0;
      var slices = 0;
      if (entry.TryGetProperty("chunks", out var chunkArray) && chunkArray.ValueKind == JsonValueKind.Array) {
        chunks = chunkArray.GetArrayLength();
        foreach (var chunk in chunkArray.EnumerateArray())
          if (chunk.TryGetProperty("slices", out var sliceArray) && sliceArray.ValueKind == JsonValueKind.Array)
            slices += sliceArray.GetArrayLength();
      }

      ++entryCount;
      if (type == "directory") ++directoryCount;
      if (type == "regular") {
        ++fileCount;
        logicalBytes += length;
      }
      chunkCount += chunks;
      sliceCount += slices;
      builder.Append(current.Path).Append('\t').Append(type).Append('\t')
        .Append(inode.ToString(CultureInfo.InvariantCulture)).Append('\t')
        .Append(length.ToString(CultureInfo.InvariantCulture)).Append('\t')
        .Append(chunks.ToString(CultureInfo.InvariantCulture)).Append('\t')
        .Append(slices.ToString(CultureInfo.InvariantCulture)).Append('\n');

      if (!entry.TryGetProperty("entries", out var children) || children.ValueKind != JsonValueKind.Object)
        continue;
      foreach (var child in children.EnumerateObject())
        stack.Push((child.Value, current.Path == "/" ? "/" + child.Name : current.Path + "/" + child.Name));
    }

    return new ManifestInfo(
      Encoding.UTF8.GetBytes(builder.ToString()), entryCount, directoryCount, fileCount,
      logicalBytes, chunkCount, sliceCount);
  }

  private static byte[] BuildMetadata(JsonElement setting, JsonElement counters, ManifestInfo manifest) {
    var builder = new StringBuilder();
    builder.AppendLine("parse_status=ok");
    builder.AppendLine("backup_kind=json");
    builder.AppendLine("format=JuiceFS metadata backup");
    AppendValue(builder, "volume_name", ReadString(setting, "Name"));
    AppendValue(builder, "volume_uuid", ReadString(setting, "UUID"));
    AppendValue(builder, "storage", ReadString(setting, "Storage"));
    AppendValue(builder, "metadata_version", ReadInt64(setting, "MetaVersion"));
    AppendValue(builder, "used_space", ReadInt64(counters, "usedSpace"));
    AppendValue(builder, "used_inodes", ReadInt64(counters, "usedInodes"));
    AppendValue(builder, "namespace_entries", manifest.EntryCount);
    AppendValue(builder, "directories", manifest.DirectoryCount);
    AppendValue(builder, "regular_files", manifest.FileCount);
    AppendValue(builder, "logical_file_bytes", manifest.LogicalBytes);
    AppendValue(builder, "chunk_records", manifest.ChunkCount);
    AppendValue(builder, "slice_records", manifest.SliceCount);
    AppendLimitations(builder, "remove insignificant JSON whitespace only; metadata values and namespace are preserved");
    return Encoding.UTF8.GetBytes(builder.ToString());
  }

  private static string? ReadString(JsonElement element, string name)
    => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
       && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

  private static long ReadInt64(JsonElement element, string name)
    => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
       && value.TryGetInt64(out var result) ? result : 0;

  private static ulong ReadUInt64(JsonElement element, string name)
    => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
       && value.TryGetUInt64(out var result) ? result : 0;

  private static void AppendValue<T>(StringBuilder builder, string key, T? value)
    => builder.Append(key).Append('=').Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\n');

  internal static void AppendLimitations(StringBuilder builder, string shrink) {
    builder.AppendLine("contains_file_payloads=false");
    builder.AppendLine("payload_location=configured JuiceFS object storage; chunk/slice records are references only");
    builder.Append("shrink=").AppendLine(shrink);
    builder.AppendLine("defrag=not applicable to a metadata backup; object-store slice compaction is a live JuiceFS operation");
    builder.AppendLine("wipe=not safe offline because dead object-store data is not represented by backup byte gaps");
    builder.AppendLine("layout=not applicable; allocation lives in the metadata engine and object store");
    builder.AppendLine("purge=not exposed offline because deleting metadata alone would not erase referenced object-store payloads");
  }

  internal static void AddGenerated(List<JuiceFsEntry> entries, string name, byte[] data)
    => entries.Add(new JuiceFsEntry { Name = name, Size = data.LongLength, Data = data });

  internal static void AddSource(List<JuiceFsEntry> entries, string name, long offset, long size)
    => entries.Add(new JuiceFsEntry { Name = name, Size = size, Offset = offset, UsesSourceData = true });

  private readonly record struct ManifestInfo(
    byte[] Data, int EntryCount, int DirectoryCount, int FileCount,
    ulong LogicalBytes, int ChunkCount, int SliceCount);
}
