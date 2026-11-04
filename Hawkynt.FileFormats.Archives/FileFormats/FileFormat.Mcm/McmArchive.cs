using System.Text;
using Compression.Core.Entropy.ContextMixing.Mcm;

namespace FileFormat.Mcm;

/// <summary>A name in an mcm file list.</summary>
internal sealed record McmFileEntry(string Name, byte Attributes) {
  public const byte DirectoryAttribute = 0x01;

  public bool IsDirectory => (this.Attributes & DirectoryAttribute) != 0;
}

/// <summary>A byte range of a file that belongs to a solid block.</summary>
internal readonly record struct McmRange(long Offset, long Length);

/// <summary>All ranges one file contributes to a solid block.</summary>
internal sealed record McmSegment(int FileIndex, long BaseOffset, IReadOnlyList<McmRange> Ranges) {
  public long TotalLength => this.Ranges.Sum(r => r.Length);
}

/// <summary>mcm's compressor type byte.</summary>
internal enum McmCompressor : byte { Store = 0, Wave16 = 1, Turbo = 2, Fast = 3, Mid = 4, High = 5, Max = 6, Simple = 7 }

/// <summary>mcm's filter byte.</summary>
internal enum McmFilter : byte { None = 0, Dictionary = 1, X86 = 2 }

/// <summary>mcm's analyzer profile byte.</summary>
internal enum McmBlockProfile : byte { Text = 0, Binary = 1, Wave16 = 2 }

/// <summary>A solid block's algorithm and the file ranges it holds.</summary>
internal sealed record McmBlock(byte MemoryLevel, McmCompressor Compressor, bool Lzp, McmFilter Filter, McmBlockProfile Profile, IReadOnlyList<McmSegment> Segments) {
  public long TotalLength => this.Segments.Sum(s => s.TotalLength);
}

/// <summary>Reads and writes mcm 0.84 archives (<c>docs/MCM-ON-DISK.md</c>, "Container").</summary>
internal static class McmArchive {
  internal static ReadOnlySpan<byte> Magic => "MCMARCHIVE"u8;
  public const ushort MajorVersion = 0;
  public const ushort MinorVersion = 84;
  internal const int MetadataMemoryLevel = 6;
  internal const int MetadataInputs = 6;
  internal const int BlockPrefix = 10;
  private const int MetadataCheck = 1234;

  /// <summary>The parsed index of an archive.</summary>
  internal sealed record Index(IReadOnlyList<McmFileEntry> Files, IReadOnlyList<McmBlock> Blocks, int PayloadStart);

  public static bool HasMagic(ReadOnlySpan<byte> data) => data.Length >= 14 && data[..10].SequenceEqual(Magic);

  /// <summary>Parses the header and the compressed metadata.</summary>
  public static Index ReadIndex(byte[] data) {
    if (!HasMagic(data))
      throw new InvalidDataException("Not an mcm archive.");
    var major = data[10] << 8 | data[11];
    var minor = data[12] << 8 | data[13];
    if (major != MajorVersion || minor != MinorVersion)
      throw new NotSupportedException($"mcm archive version {major}.{minor}; only 0.84 is supported.");
    var position = 14;
    var metadataLength = ReadLeb(data, ref position);
    if (metadataLength > int.MaxValue)
      throw new InvalidDataException("mcm metadata length is implausible.");
    var coder = new McmContextMixer(MetadataInputs, McmModelProfile.Text, MetadataMemoryLevel, lzp: true);
    var metadata = coder.Decode(data, position, data.Length, (int)metadataLength, out var used);
    position += used;
    if (ReadLeb(data, ref position) != MetadataCheck)
      throw new InvalidDataException("mcm metadata check value is wrong.");

    var m = 0;
    var blockCount = ReadLeb(metadata, ref m);
    if (blockCount > 1_000_000)
      throw new InvalidDataException("mcm block count is implausible.");
    var blocks = new List<McmBlock>();
    for (var b = 0L; b < blockCount; ++b) {
      Need(metadata, m, 5);
      var memory = metadata[m++];
      var compressor = (McmCompressor)metadata[m++];
      var lzp = metadata[m++] != 0;
      var filter = (McmFilter)metadata[m++];
      var profile = (McmBlockProfile)metadata[m++];
      var segmentCount = ReadLeb(metadata, ref m);
      if (segmentCount > 10_000_000)
        throw new InvalidDataException("mcm segment count is implausible.");
      var segments = new List<McmSegment>();
      for (var s = 0L; s < segmentCount; ++s) {
        var fileIndex = ReadLeb(metadata, ref m);
        var baseOffset = ReadLeb(metadata, ref m);
        var rangeCount = ReadLeb(metadata, ref m);
        if (rangeCount > 10_000_000)
          throw new InvalidDataException("mcm range count is implausible.");
        var lengths = new long[rangeCount];
        for (var r = 0; r < rangeCount; ++r)
          lengths[r] = ReadLeb(metadata, ref m);
        var ranges = new McmRange[rangeCount];
        var previousEnd = 0L;
        for (var r = 0; r < rangeCount; ++r) {
          var offset = previousEnd + ReadLeb(metadata, ref m);
          ranges[r] = new McmRange(offset, lengths[r]);
          previousEnd = offset + lengths[r];
        }
        segments.Add(new McmSegment((int)fileIndex, baseOffset, ranges));
      }
      blocks.Add(new McmBlock(memory, compressor, lzp, filter, profile, segments));
    }

    var fileCount = ReadLeb(metadata, ref m);
    if (fileCount > 10_000_000)
      throw new InvalidDataException("mcm file count is implausible.");
    var suffixes = new byte[fileCount][];
    for (var f = 0; f < fileCount; ++f) {
      var end = metadata.AsSpan(m).IndexOf((byte)0);
      if (end < 0)
        throw new InvalidDataException("mcm file name is not terminated.");
      suffixes[f] = metadata.AsSpan(m, end).ToArray();
      m += end + 1;
    }
    var names = new byte[fileCount][];
    for (var f = 0; f < fileCount; ++f) {
      var shared = (int)ReadLeb(metadata, ref m);
      names[f] = f > 0 && shared > 0
        ? [.. names[f - 1].AsSpan(0, Math.Min(shared, names[f - 1].Length)), .. suffixes[f]]
        : suffixes[f];
    }
    var files = new List<McmFileEntry>();
    for (var f = 0; f < fileCount; ++f) {
      Need(metadata, m, 1);
      files.Add(new McmFileEntry(DecodeName(names[f]), metadata[m++]));
    }
    foreach (var block in blocks)
      foreach (var segment in block.Segments)
        if ((uint)segment.FileIndex >= (uint)files.Count)
          throw new InvalidDataException("mcm segment names a file that is not listed.");
    return new Index(files, blocks, position);
  }

  /// <summary>Size of each listed file: the end of its furthest range.</summary>
  public static long[] FileSizes(Index index) {
    var sizes = new long[index.Files.Count];
    foreach (var block in index.Blocks)
      foreach (var segment in block.Segments)
        foreach (var range in segment.Ranges)
          sizes[segment.FileIndex] = Math.Max(sizes[segment.FileIndex], segment.BaseOffset + range.Offset + range.Length);
    return sizes;
  }

  /// <summary>Decodes every block and returns the content of each listed file.</summary>
  public static byte[][] Extract(byte[] data, Index index) {
    var sizes = FileSizes(index);
    var contents = new byte[sizes.Length][];
    for (var f = 0; f < sizes.Length; ++f)
      contents[f] = sizes[f] > int.MaxValue ? throw new NotSupportedException("mcm file larger than 2 GiB.") : new byte[sizes[f]];
    var position = index.PayloadStart;
    foreach (var block in index.Blocks) {
      var plain = DecodeBlock(data, ref position, block);
      var p = 0;
      foreach (var segment in block.Segments)
        foreach (var range in segment.Ranges) {
          plain.AsSpan(p, (int)range.Length).CopyTo(contents[segment.FileIndex].AsSpan((int)(segment.BaseOffset + range.Offset)));
          p += (int)range.Length;
        }
    }
    return contents;
  }

  private static byte[] DecodeBlock(byte[] data, ref int position, McmBlock block) {
    var total = block.TotalLength;
    if (total > int.MaxValue)
      throw new NotSupportedException("mcm solid block larger than 2 GiB.");
    var prefix = position;
    var filteredLength = ReadLeb(data, ref prefix);
    if (filteredLength > int.MaxValue || position + BlockPrefix > data.Length)
      throw new InvalidDataException("mcm block header is invalid.");
    var start = position + BlockPrefix;
    byte[] filtered;
    int consumed;
    switch (block.Compressor) {
      case McmCompressor.Store:
        if (start + filteredLength > data.Length)
          throw new InvalidDataException("mcm stored block is truncated.");
        filtered = data.AsSpan(start, (int)filteredLength).ToArray();
        consumed = (int)filteredLength;
        break;
      case McmCompressor.Wave16:
        filtered = McmWave16Coder.Decode(data, start, data.Length, (int)filteredLength, out consumed);
        break;
      case McmCompressor.Max:
        throw new NotSupportedException("mcm -x (max) blocks: mcm 0.84 crashes when writing them, so no reference exists to prove a decoder against.");
      case McmCompressor.Turbo or McmCompressor.Fast or McmCompressor.Mid or McmCompressor.High or McmCompressor.Simple:
        if (block.MemoryLevel > McmContextMixer.MaxMemoryLevel)
          throw new NotSupportedException($"mcm memory level {block.MemoryLevel} needs a hash table larger than 2 GiB.");
        var coder = new McmContextMixer(Inputs(block.Compressor), ModelProfile(block), block.MemoryLevel, block.Lzp);
        filtered = coder.Decode(data, start, data.Length, (int)filteredLength, out consumed);
        break;
      default:
        throw new InvalidDataException($"Unknown mcm compressor type {(byte)block.Compressor}.");
    }
    position = start + consumed;
    return block.Filter switch {
      McmFilter.None => filtered.Length == total ? filtered : throw new InvalidDataException("mcm block length does not match its segments."),
      McmFilter.Dictionary => McmDictionaryFilter.Decode(filtered, (int)total),
      McmFilter.X86 => McmX86Filter.Decode(filtered, (int)total),
      _ => throw new InvalidDataException($"Unknown mcm filter {(byte)block.Filter}."),
    };
  }

  internal static int Inputs(McmCompressor compressor) => compressor switch {
    McmCompressor.Turbo => 3,
    McmCompressor.Fast => 4,
    McmCompressor.Mid or McmCompressor.Simple => 6,
    McmCompressor.High => 10,
    _ => throw new ArgumentOutOfRangeException(nameof(compressor)),
  };

  internal static McmModelProfile ModelProfile(McmBlock block) => block.Compressor == McmCompressor.Simple
    ? McmModelProfile.Simple
    : block.Profile == McmBlockProfile.Text ? McmModelProfile.Text : McmModelProfile.Binary;

  private static string DecodeName(byte[] name) {
    try {
      return new UTF8Encoding(false, true).GetString(name);
    } catch (DecoderFallbackException) {
      return Encoding.Latin1.GetString(name);
    }
  }

  internal static long ReadLeb(byte[] data, ref int position) {
    long value = 0;
    for (var shift = 0; ; shift += 7) {
      Need(data, position, 1);
      if (shift > 63)
        throw new InvalidDataException("mcm LEB128 value is too long.");
      var b = data[position++];
      value |= (long)(b & 0x7F) << shift;
      if ((b & 0x80) == 0)
        return value;
    }
  }

  internal static void WriteLeb(Stream stream, long value) {
    var v = (ulong)value;
    while (v >= 0x80) {
      stream.WriteByte((byte)(0x80 | (v & 0x7F)));
      v >>= 7;
    }
    stream.WriteByte((byte)v);
  }

  private static void Need(byte[] data, int position, int count) {
    if (position + count > data.Length)
      throw new InvalidDataException("mcm archive is truncated.");
  }
}
