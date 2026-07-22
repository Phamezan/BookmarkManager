using System;
using System.Linq;

namespace BookmarkManager.Api.Services.Rerank;

/// <summary>Splices a separately-tokenized query and passage into the single joint sequence a
/// cross-encoder needs (<c>[CLS] query [SEP] passage [SEP]</c> for BERT-family tokenizers, or the
/// equivalent RoBERTa/XLM-R shape), driven entirely by the pair template read into
/// <see cref="RerankerTokenizerAssets"/>. Takes a plain <c>Func&lt;string, uint[]&gt;</c> encode delegate
/// rather than depending on <c>Tokenizers.DotNet.Tokenizer</c> directly, so the splicing logic is testable
/// without a real tokenizer/model on disk.</summary>
public static class RerankerPairEncoder
{
    public static (uint[] InputIds, long[] TypeIds) Encode(
        Func<string, uint[]> encode,
        RerankerTokenizerAssets assets,
        string query,
        string passage,
        int maxSequenceLength)
    {
        ArgumentNullException.ThrowIfNull(encode);
        ArgumentNullException.ThrowIfNull(assets);

        var queryIds = StripSingleWrap(encode(query ?? string.Empty), assets);
        var passageIds = StripSingleWrap(encode(passage ?? string.Empty), assets);
        return BuildPair(queryIds, passageIds, assets, maxSequenceLength);
    }

    /// <summary><c>Tokenizer.Encode</c> always runs the tokenizer.json post_processor, which wraps a lone
    /// sequence in the "single" template's special tokens. Building our own pair sequence needs the bare
    /// subword ids underneath, so this strips exactly what the template says was added - verifying the ids
    /// actually match rather than blindly slicing positions, so a wrong assumption throws instead of
    /// silently corrupting the encoding (the worst failure mode: plausible-looking garbage scores).</summary>
    internal static uint[] StripSingleWrap(uint[] ids, RerankerTokenizerAssets assets)
    {
        var lead = assets.LeadingSingleIds;
        var trail = assets.TrailingSingleIds;
        if (ids.Length < lead.Count + trail.Count)
        {
            throw new InvalidOperationException(
                "Encoded sequence is shorter than the tokenizer's single-template wrap; cannot strip special tokens.");
        }

        for (var i = 0; i < lead.Count; i++)
        {
            if (ids[i] != lead[i])
            {
                throw new InvalidOperationException(
                    $"Expected leading special token {lead[i]} at position {i} but found {ids[i]}; the tokenizer's " +
                    "single-template assumption no longer matches Tokenizer.Encode's actual output.");
            }
        }

        for (var i = 0; i < trail.Count; i++)
        {
            var pos = ids.Length - trail.Count + i;
            if (ids[pos] != trail[i])
            {
                throw new InvalidOperationException(
                    $"Expected trailing special token {trail[i]} at position {pos} but found {ids[pos]}; the " +
                    "tokenizer's single-template assumption no longer matches Tokenizer.Encode's actual output.");
            }
        }

        return ids[lead.Count..(ids.Length - trail.Count)];
    }

    /// <summary>Splices the bare query/passage ids into the exact shape tokenizer.json's
    /// <c>post_processor.pair</c> template describes, truncating only the passage (never the query) to
    /// fit <paramref name="maxSequenceLength"/>.</summary>
    internal static (uint[] InputIds, long[] TypeIds) BuildPair(
        uint[] queryIds, uint[] passageIds, RerankerTokenizerAssets assets, int maxSequenceLength)
    {
        var specialCount = assets.PairTemplate.Count(s => s.Kind == PairStepKind.SpecialToken);
        // Budget can go negative if the query alone (plus template overhead) already exceeds the model's
        // sequence limit; Clamp floors that at 0 rather than throwing, since the contract is "truncate the
        // passage, never the query" - an oversized query is the caller's problem, not this method's.
        var budget = maxSequenceLength - specialCount - queryIds.Length;
        var passageTake = Math.Clamp(budget, 0, passageIds.Length);

        var totalLength = specialCount + queryIds.Length + passageTake;
        var outIds = new uint[totalLength];
        var outTypes = new long[totalLength];
        var cursor = 0;

        foreach (var step in assets.PairTemplate)
        {
            switch (step.Kind)
            {
                case PairStepKind.SpecialToken:
                    outIds[cursor] = step.SpecialTokenId;
                    outTypes[cursor] = step.TypeId;
                    cursor++;
                    break;
                case PairStepKind.SequenceA:
                    Array.Copy(queryIds, 0, outIds, cursor, queryIds.Length);
                    Array.Fill(outTypes, (long)step.TypeId, cursor, queryIds.Length);
                    cursor += queryIds.Length;
                    break;
                case PairStepKind.SequenceB:
                    Array.Copy(passageIds, 0, outIds, cursor, passageTake);
                    Array.Fill(outTypes, (long)step.TypeId, cursor, passageTake);
                    cursor += passageTake;
                    break;
            }
        }

        return (outIds, outTypes);
    }
}
