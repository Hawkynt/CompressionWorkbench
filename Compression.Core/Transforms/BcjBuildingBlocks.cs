using Compression.Registry;

namespace Compression.Core.Transforms;

// Branch/Call/Jump (BCJ) filters exposed as benchmarkable building blocks.
// Each BCJ filter is a bijective byte transform: "Compress" applies the encode
// filter, "Decompress" applies the decode filter. BCJ2 is intentionally omitted
// because it is a 4-stream filter that does not fit the single-buffer shape.

/// <summary>Exposes the x86 BCJ filter as a building block.</summary>
public sealed class BcjX86BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_BcjX86";
  /// <inheritdoc/>
  public string DisplayName => "BCJ x86";
  /// <inheritdoc/>
  public string Description => "x86 branch/call/jump filter, converts relative CALL/JMP targets to absolute";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;
  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => BcjFilter.EncodeX86(data);
  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => BcjFilter.DecodeX86(data);
}

/// <summary>Exposes the ARM BCJ filter as a building block.</summary>
public sealed class BcjArmBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_BcjArm";
  /// <inheritdoc/>
  public string DisplayName => "BCJ ARM";
  /// <inheritdoc/>
  public string Description => "32-bit ARM branch/call/jump filter, converts relative BL targets to absolute";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;
  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => BcjFilter.EncodeArm(data);
  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => BcjFilter.DecodeArm(data);
}

/// <summary>Exposes the ARM Thumb BCJ filter as a building block.</summary>
public sealed class BcjArmThumbBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_BcjArmThumb";
  /// <inheritdoc/>
  public string DisplayName => "BCJ ARM Thumb";
  /// <inheritdoc/>
  public string Description => "ARM Thumb branch/call/jump filter, converts relative BL targets to absolute";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;
  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => BcjFilter.EncodeArmThumb(data);
  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => BcjFilter.DecodeArmThumb(data);
}

/// <summary>Exposes the ARM64 (AArch64) BCJ filter as a building block.</summary>
public sealed class BcjArm64BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_BcjArm64";
  /// <inheritdoc/>
  public string DisplayName => "BCJ ARM64";
  /// <inheritdoc/>
  public string Description => "ARM64/AArch64 branch/call/jump filter, converts relative BL and ADRP targets to absolute";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;
  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => BcjFilter.EncodeArm64(data);
  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => BcjFilter.DecodeArm64(data);
}

/// <summary>Exposes the PowerPC BCJ filter as a building block.</summary>
public sealed class BcjPowerPcBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_BcjPowerPc";
  /// <inheritdoc/>
  public string DisplayName => "BCJ PowerPC";
  /// <inheritdoc/>
  public string Description => "PowerPC branch/call/jump filter, converts relative B/BL targets to absolute";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;
  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => BcjFilter.EncodePowerPC(data);
  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => BcjFilter.DecodePowerPC(data);
}

/// <summary>Exposes the SPARC BCJ filter as a building block.</summary>
public sealed class BcjSparcBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_BcjSparc";
  /// <inheritdoc/>
  public string DisplayName => "BCJ SPARC";
  /// <inheritdoc/>
  public string Description => "SPARC branch/call/jump filter, converts relative CALL targets to absolute";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;
  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => BcjFilter.EncodeSparc(data);
  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => BcjFilter.DecodeSparc(data);
}

/// <summary>Exposes the IA-64 (Itanium) BCJ filter as a building block.</summary>
public sealed class BcjIa64BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_BcjIa64";
  /// <inheritdoc/>
  public string DisplayName => "BCJ IA-64";
  /// <inheritdoc/>
  public string Description => "IA-64 (Itanium) branch/call/jump filter, converts relative branch targets to absolute";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;
  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => BcjFilter.EncodeIA64(data);
  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => BcjFilter.DecodeIA64(data);
}

/// <summary>Exposes the RISC-V BCJ filter as a building block.</summary>
public sealed class BcjRiscVBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_BcjRiscV";
  /// <inheritdoc/>
  public string DisplayName => "BCJ RISC-V";
  /// <inheritdoc/>
  public string Description => "RISC-V branch/call/jump filter, converts JAL and AUIPC+inst2 pc-relative references to absolute";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;
  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => BcjFilter.EncodeRiscV(data);
  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => BcjFilter.DecodeRiscV(data);
}
