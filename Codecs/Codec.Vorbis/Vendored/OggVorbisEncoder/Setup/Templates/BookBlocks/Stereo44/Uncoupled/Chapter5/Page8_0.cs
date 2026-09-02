namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter5;

/// <summary>
/// Represents a page 8 0.
/// </summary>
public class Page8_0 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         1, 4, 4, 6, 6, 8, 8, 9, 9,10,10, 4, 6, 6, 7, 7,
         9, 9,10,10,11,11, 4, 6, 6, 7, 7, 9, 9,10,10,11,
        11, 6, 8, 7, 9, 9,10,10,11,11,13,12, 6, 8, 8, 9,
         9,10,10,11,11,12,13, 8, 9, 9,10,10,12,12,13,12,
        14,13, 8, 9, 9,10,10,12,12,13,13,14,14, 9,11,11,
        12,12,13,13,14,14,15,14, 9,11,11,12,12,13,13,14,
        14,15,14,11,12,12,13,13,14,14,15,14,15,14,11,11,
        12,13,13,14,14,14,14,15,15,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
    public int QuantMin { get; } = -524582912;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
    public int QuantDelta { get; } = 1618345984;
    /// <summary>
    /// Gets the quant.
    /// </summary>
    public int Quant { get; } = 4;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
    public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
    public int[] QuantList { get; } = {
        5,
        4,
        6,
        3,
        7,
        2,
        8,
        1,
        9,
        0,
        10,
    };
}
