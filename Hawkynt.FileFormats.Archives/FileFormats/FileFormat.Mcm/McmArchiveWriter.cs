using System.Text;
using Compression.Core.Entropy.ContextMixing.Mcm;

namespace FileFormat.Mcm;

/// <summary>An entry to put into an mcm archive.</summary>
internal sealed record McmInput(string Name, byte[] Data, bool IsDirectory = false);

/// <summary>
/// Writes mcm 0.84 archives the way <c>mcm -{t|f|m|h|s}N</c> does (<c>docs/MCM-ON-DISK.md</c>,
/// "What the writer puts there"): same file order, same analyzer runs, same dictionary, same
/// filters and coders, so the bytes match mcm's own for the same input.
/// </summary>
internal static class McmArchiveWriter {
  /// <summary>Attributes the Windows build stores for a writable file.</summary>
  public const byte FileAttributes = 0x0E;

  /// <summary>Attributes the Windows build stores for a writable directory.</summary>
  public const byte DirectoryAttributes = 0x05;

  public static byte[] Write(IReadOnlyList<McmInput> inputs, McmCompressor compressor, int memoryLevel) {
    if (compressor is McmCompressor.Max or McmCompressor.Wave16)
      throw new NotSupportedException("mcm writes -x archives only by crashing, and Wav16 blocks its own decoder cannot read; neither is offered.");
    if (memoryLevel is < 0 or > McmContextMixer.MaxMemoryLevel)
      throw new NotSupportedException($"mcm memory level {memoryLevel} is not supported; use 0..{McmContextMixer.MaxMemoryLevel}.");

    var entries = inputs.Select(i => (Input: i, Name: Encoding.UTF8.GetBytes(i.Name.Replace('\\', '/').TrimEnd('/')))).ToArray();
    McmIntroSort.Sort(entries, (a, b) => Precedes(a.Name, a.Input.IsDirectory, b.Name, b.Input.IsDirectory));

    var words = new McmDictionaryBuilder();
    var textSegments = new List<McmSegment>();
    var binarySegments = new List<McmSegment>();
    for (var index = 0; index < entries.Length; ++index) {
      var input = entries[index].Input;
      if (input.IsDirectory)
        continue;
      var runs = McmAnalyzer.Analyze(input.Data, words);
      if (runs.Count == 0)
        runs.Add((McmBlockProfile.Binary, 0));
      foreach (var (profile, segments) in new[] { (McmBlockProfile.Text, textSegments), (McmBlockProfile.Binary, binarySegments) }) {
        var ranges = new List<McmRange>();
        var position = 0L;
        foreach (var run in runs) {
          if (run.Profile == profile)
            ranges.Add(new McmRange(position, run.Length));
          position += run.Length;
        }
        if (ranges.Count > 0)
          segments.Add(new McmSegment(index, 0, ranges));
      }
    }

    var lzp = true;
    var blocks = new List<McmBlock> {
      new((byte)memoryLevel, compressor, lzp, McmFilter.Dictionary, McmBlockProfile.Text, textSegments),
      new((byte)memoryLevel, compressor, lzp, McmFilter.X86, McmBlockProfile.Binary, binarySegments),
    };
    blocks.RemoveAll(b => b.TotalLength == 0);
    blocks = [.. blocks.OrderByDescending(b => b.TotalLength)];

    // Blocks are coded before the index is written: a binary block that took the fast path is
    // recoded with the text model set or stored, whichever is smaller — the least change that
    // keeps it readable by mcm itself.
    var data = entries.Select(e => e.Input.Data).ToArray();
    var payloads = new List<(byte[] Filtered, byte[] Coded)>();
    for (var b = 0; b < blocks.Count; ++b) {
      var block = blocks[b];
      var plain = Gather(block, data);
      var filtered = block.Filter == McmFilter.Dictionary
        ? McmDictionaryFilter.Encode(plain, words.Build())
        : McmX86Filter.Encode(plain);
      var coded = filtered;
      if (block.Compressor != McmCompressor.Store) {
        var coder = new McmContextMixer(McmArchive.Inputs(block.Compressor), McmArchive.ModelProfile(block), block.MemoryLevel, block.Lzp);
        coded = coder.Encode(filtered);
        if (coder.UsedFastPath) {
          var text = block with { Profile = McmBlockProfile.Text };
          coded = new McmContextMixer(McmArchive.Inputs(text.Compressor), McmArchive.ModelProfile(text), text.MemoryLevel, text.Lzp).Encode(filtered);
          blocks[b] = text;
          if (coded.Length >= filtered.Length) {
            coded = filtered;
            blocks[b] = block with { Compressor = McmCompressor.Store };
          }
        }
      }
      payloads.Add((filtered, coded));
    }

    using var output = new MemoryStream();
    output.Write(McmArchive.Magic);
    output.Write([McmArchive.MajorVersion >> 8, McmArchive.MajorVersion & 0xFF, McmArchive.MinorVersion >> 8, McmArchive.MinorVersion & 0xFF]);
    var metadata = Metadata(blocks, entries.Select(e => (e.Name, e.Input.IsDirectory)).ToList());
    McmArchive.WriteLeb(output, metadata.Length);
    output.Write(new McmContextMixer(McmArchive.MetadataInputs, McmModelProfile.Text, McmArchive.MetadataMemoryLevel, lzp: true).Encode(metadata));
    McmArchive.WriteLeb(output, 1234);
    foreach (var (filtered, coded) in payloads) {
      var prefix = new MemoryStream();
      McmArchive.WriteLeb(prefix, filtered.Length);
      while (prefix.Length < McmArchive.BlockPrefix)
        prefix.WriteByte(0);
      prefix.WriteTo(output);
      output.Write(coded);
    }
    return output.ToArray();
  }

  private static byte[] Gather(McmBlock block, byte[][] data) {
    var plain = new byte[block.TotalLength];
    var p = 0;
    foreach (var segment in block.Segments)
      foreach (var range in segment.Ranges) {
        data[segment.FileIndex].AsSpan((int)range.Offset, (int)range.Length).CopyTo(plain.AsSpan(p));
        p += (int)range.Length;
      }
    return plain;
  }

  private static byte[] Metadata(List<McmBlock> blocks, List<(byte[] Name, bool IsDirectory)> files) {
    using var stream = new MemoryStream();
    McmArchive.WriteLeb(stream, blocks.Count);
    foreach (var block in blocks) {
      stream.Write([block.MemoryLevel, (byte)block.Compressor, (byte)(block.Lzp ? 1 : 0), (byte)block.Filter, (byte)block.Profile]);
      McmArchive.WriteLeb(stream, block.Segments.Count);
      foreach (var segment in block.Segments) {
        McmArchive.WriteLeb(stream, segment.FileIndex);
        McmArchive.WriteLeb(stream, segment.BaseOffset);
        McmArchive.WriteLeb(stream, segment.Ranges.Count);
        foreach (var range in segment.Ranges)
          McmArchive.WriteLeb(stream, range.Length);
        var previousEnd = 0L;
        foreach (var range in segment.Ranges) {
          McmArchive.WriteLeb(stream, range.Offset - previousEnd);
          previousEnd = range.Offset + range.Length;
        }
      }
    }
    McmArchive.WriteLeb(stream, files.Count);
    var shared = new List<int>();
    byte[]? previous = null;
    foreach (var (name, _) in files) {
      if (name.Length == 0)
        throw new ArgumentException("mcm cannot store an empty file name.");
      var common = 0;
      if (previous != null)
        while (common < previous.Length && common < name.Length && previous[common] == name[common])
          ++common;
      shared.Add(common);
      stream.Write(name.AsSpan(common));
      stream.WriteByte(0);
      previous = name;
    }
    foreach (var common in shared)
      McmArchive.WriteLeb(stream, common);
    foreach (var (_, isDirectory) in files)
      stream.WriteByte(isDirectory ? DirectoryAttributes : FileAttributes);
    return stream.ToArray();
  }

  /// <summary>mcm's file order: directories first by name, then files by sort extension, file name, path.</summary>
  private static bool Precedes(byte[] a, bool aDirectory, byte[] b, bool bDirectory) {
    if (aDirectory != bDirectory)
      return aDirectory;
    if (aDirectory)
      return a.AsSpan().SequenceCompareTo(b) < 0;
    var extensionOrder = SortExtension(a).AsSpan().SequenceCompareTo(SortExtension(b));
    if (extensionOrder != 0)
      return extensionOrder < 0;
    var nameOrder = FileName(a).SequenceCompareTo(FileName(b));
    if (nameOrder != 0)
      return nameOrder < 0;
    return a.AsSpan().SequenceCompareTo(b) < 0;
  }

  private static ReadOnlySpan<byte> FileName(byte[] path) {
    var slash = path.AsSpan().LastIndexOfAny((byte)'/', (byte)'\\');
    return path.AsSpan(slash + 1);
  }

  private static byte[] SortExtension(byte[] path) {
    var extension = Array.Empty<byte>();
    for (var i = path.Length; i > 0; --i) {
      var c = path[i - 1];
      if (c is (byte)'\\' or (byte)'/')
        break;
      if (c == '.') {
        extension = path[i..];
        break;
      }
    }
    var text = Encoding.Latin1.GetString(extension);
    return text switch {
      "h" or "hpp" or "inl" or "cpp" => "c"u8.ToArray(),
      "jpg" or "zip" or "7z" or "apk" or "mp3" or "gif" or "png" => [0xFF, .. extension],
      _ => extension,
    };
  }
}
