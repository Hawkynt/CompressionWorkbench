#pragma warning disable CS1591
namespace FileFormat.OneNote;

public enum OneNoteVariant {
  Unknown,
  OneNote2007,
  OneNote2010Plus,
}

public static class OneNoteDetector {

  // {7B5C52E4-D88C-4DA7-AEB1-5378D02996D3} — OneNote 2010+ section file GUID.
  // Stored as Microsoft GUID layout: Data1 (uint32 LE), Data2/3 (uint16 LE), Data4 (8 bytes raw).
  internal static readonly byte[] Guid2010Plus = [
    0xE4, 0x52, 0x5C, 0x7B,
    0x8C, 0xD8,
    0xA7, 0x4D,
    0xAE, 0xB1, 0x53, 0x78, 0xD0, 0x29, 0x96, 0xD3,
  ];

  // {109ADD3F-911B-49F5-A5D0-1791EDC8AED8} — OneNote 2007 section file GUID.
  internal static readonly byte[] Guid2007 = [
    0x3F, 0xDD, 0x9A, 0x10,
    0x1B, 0x91,
    0xF5, 0x49,
    0xA5, 0xD0, 0x17, 0x91, 0xED, 0xC8, 0xAE, 0xD8,
  ];

  public static OneNoteVariant Detect(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    var origin = stream.Position;
    try {
      stream.Seek(0, SeekOrigin.Begin);
      Span<byte> head = stackalloc byte[16];
      var read = 0;
      while (read < 16) {
        var n = stream.Read(head[read..]);
        if (n <= 0) break;
        read += n;
      }
      if (read < 16) return OneNoteVariant.Unknown;

      if (head.SequenceEqual(Guid2010Plus)) return OneNoteVariant.OneNote2010Plus;
      if (head.SequenceEqual(Guid2007)) return OneNoteVariant.OneNote2007;
      return OneNoteVariant.Unknown;
    } finally {
      stream.Seek(origin, SeekOrigin.Begin);
    }
  }
}
