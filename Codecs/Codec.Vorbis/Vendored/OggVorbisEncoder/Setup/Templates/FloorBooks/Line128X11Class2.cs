namespace OggVorbisEncoder.Setup.Templates.FloorBooks;

/// <summary>
/// Represents a line 128 x 11 class 2.
/// </summary>
public class Line128X11Class2 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 1;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
        1, 6, 12, 16, 4, 12, 15, 16, 9, 15, 16, 16, 16, 16, 16, 16,
        2, 5, 11, 16, 5, 11, 13, 16, 9, 13, 16, 16, 16, 16, 16, 16,
        4, 8, 12, 16, 5, 9, 12, 16, 9, 13, 15, 16, 16, 16, 16, 16,
        15, 16, 16, 16, 11, 14, 13, 16, 12, 15, 16, 16, 16, 16, 16, 15
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = CodeBookMapType.None;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
    public int QuantMin { get; } = 0;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
    public int QuantDelta { get; } = 0;
    /// <summary>
    /// Gets the quant.
    /// </summary>
    public int Quant { get; } = 0;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
    public int QuantSequenceP { get; } = 0;
    /// <summary>
    /// Gets the quant list.
    /// </summary>
    public int[] QuantList { get; } = null;
}
