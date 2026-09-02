namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.ManagedChapter0;

/// <summary>
/// Represents a page 6 0.
/// </summary>
public class Page6_0 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 4;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         1, 4, 4, 7, 6, 6, 7, 6, 6, 4, 7, 7,10, 9, 9,11,
         9, 9, 4, 7, 7,10, 9, 9,11, 9, 9, 7,10,10,10,11,
        11,11,10,10, 6, 9, 9,11,11,10,11,10,10, 6, 9, 9,
        11,10,11,11,10,10, 7,11,10,11,11,11,11,11,11, 6,
         9, 9,11,10,10,11,11,10, 6, 9, 9,11,10,10,11,10,
        11,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
    public int QuantMin { get; } = -529137664;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
    public int QuantDelta { get; } = 1618345984;
    /// <summary>
    /// Gets the quant.
    /// </summary>
    public int Quant { get; } = 2;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
    public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
    public int[] QuantList { get; } = {
        1,
        0,
        2,
    };
}
