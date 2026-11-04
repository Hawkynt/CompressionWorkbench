namespace FileFormat.Uharc;

/// <summary>
/// Undoes the delta coding UHARC's LZP mode applies to uncompressed BMP, WAV and VOC files
/// (docs/UHARC-ON-DISK.md, "Multimedia filter").
/// </summary>
internal static class UharcMultimediaFilter {

  private const int HeaderLength = 36;
  private const int Any = -1;

  private static readonly int[] Bitmap = [
    0x42, 0x4D, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any,
    Any, Any, Any, Any, Any, Any, Any, Any, 0x01, 0x00, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, Any, Any,
  ];

  private static readonly int[] Wave = [
    0x52, 0x49, 0x46, 0x46, Any, Any, Any, Any, 0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74, 0x20, Any, Any,
    Any, Any, 0x01, 0x00, Any, 0x00, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, Any, 0x00,
  ];

  private static readonly int[] Voice = [
    0x43, 0x72, 0x65, 0x61, 0x74, 0x69, 0x76, 0x65, 0x20, 0x56, 0x6F, 0x69, 0x63, 0x65, 0x20, 0x46, 0x69, 0x6C,
    0x65, 0x1A, 0x1A, 0x00, 0x0A, 0x01, 0x29, 0x11, 0x01, Any, Any, Any, Any, 0x00, Any, Any, Any, Any,
  ];

  public static void Restore(Span<byte> data) {
    if (data.Length < 1024)
      return;

    int stride;
    if (Matches(data, Bitmap))
      stride = 3;
    else if (Matches(data, Wave)) {
      stride = data[22];
      if (stride is not (1 or 2))
        return;
      if (data[34] == 16)
        stride *= 2;
      else if (data[34] != 8)
        return;
    } else if (Matches(data, Voice))
      stride = 1;
    else
      return;

    Span<bool> negate = stackalloc bool[4];
    negate.Clear();
    var shift = stride * 8 - 8;
    var history = 0u;
    for (var i = HeaderLength; i < data.Length; ++i) {
      var phase = i % stride;
      var raw = data[i];
      var delta = negate[phase] ? (byte)-raw : raw;
      if (raw >= 0x80)
        negate[phase] = !negate[phase];
      data[i] = (byte)(delta + (history >> shift));
      history = (history << 8) | data[i];
    }
  }

  private static bool Matches(ReadOnlySpan<byte> data, int[] template) {
    for (var i = 0; i < HeaderLength; ++i)
      if (template[i] != Any && template[i] != data[i])
        return false;
    return true;
  }
}
