using System;

namespace BookmarkManager.Api.Services.Rerank;

/// <summary>Presentation helper for cross-encoder logits. The raw logit is the valid ranking score
/// (candidates are independent pairs, never softmax them against each other) - this is only for turning a
/// single logit into a 0..1 number when a UI needs to display one.</summary>
public static class RerankScoring
{
    public static float Sigmoid(float logit) => 1f / (1f + MathF.Exp(-logit));
}
