namespace OggVorbisEncoder.Setup.Templates.FloorBooks;

/// <summary>
/// Represents a line 2048 x 27 2 sub 1.
/// </summary>
public class Line2048X27_2Sub1 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 1;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        3, 4, 3, 4, 3, 4, 4, 5, 4, 5, 5, 5, 6, 6, 6, 7,
        6, 8, 6, 8, 6, 9, 7, 10, 7, 10, 7, 10, 7, 12, 7, 12,
        7, 12, 9, 12, 11, 12, 10, 12, 10, 12, 11, 12, 12, 12, 10, 12,
        10, 12, 10, 12, 9, 12, 11, 12, 12, 12, 12, 12, 11, 12, 11, 12,
        12, 12, 12, 12, 12, 12, 12, 12, 10, 10, 12, 12, 12, 12, 12, 10,
        12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12
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
