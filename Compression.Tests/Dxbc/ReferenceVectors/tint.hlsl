// Pixel shader for the DXBC reference vectors: one constant buffer, one texture, one sampler.
cbuffer Params : register(b0) { float4 Tint; };
Texture2D Source : register(t0);
SamplerState Linear : register(s0);

float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target {
  return Source.Sample(Linear, uv) * Tint;
}
