namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.ManagedChapter0;

/// <summary>
/// Represents a managed chapter 0 short.
/// </summary>
public class ManagedChapter0Short : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         6, 6,12,13,13,14,16,17,17, 4, 2, 5, 8, 7, 9,12,
        15,15, 9, 4, 5, 9, 7, 9,12,16,18,11, 6, 7, 4, 6,
         8,11,14,18,10, 5, 6, 5, 5, 7,10,14,17,10, 5, 7,
         7, 6, 7,10,13,16,11, 5, 7, 7, 7, 8,10,12,15,13,
         6, 7, 5, 5, 7, 9,12,13,16, 8, 9, 6, 6, 7, 9,10,
        12,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = (CodeBookMapType)0;
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
