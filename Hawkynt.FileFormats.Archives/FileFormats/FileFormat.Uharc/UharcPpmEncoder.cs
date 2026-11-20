namespace FileFormat.Uharc;

/// <summary>
/// The encoder side of the PPM stream (docs/UHARC-ON-DISK.md, "PPM" and "Writing"): it runs the
/// decoder's <see cref="UharcPpmModel{TCoder}"/> over the input, which then answers every coding
/// decision from the byte that comes next — a predicted copy is taken when it reproduces the
/// input past its length floor, every literal stage codes the byte or escapes. It writes a binary
/// stream (no word transform) with multimedia class 0.
/// </summary>
internal static class UharcPpmEncoder {

  /// <summary>Packs <paramref name="data"/> as PPM stream <paramref name="method"/> (24..39).</summary>
  public static byte[] Encode(byte[] data, int method, IReadOnlyList<long> fileEnds) {
    if (method is < UharcMethods.PpmFirst or > UharcMethods.PpmLast)
      throw new ArgumentOutOfRangeException(nameof(method));
    if (data.Length == 0)
      return [];

    var coder = new UharcRangeEncoder();
    var model = new UharcPpmModel<UharcEncoding>(new(coder), method) { Source = data };
    var output = new byte[data.Length];
    model.Run(output, fileEnds);
    if (!output.AsSpan().SequenceEqual(data))
      throw new InvalidOperationException("UHARC: PPM encoder lost track of its model.");
    return coder.Finish();
  }
}
