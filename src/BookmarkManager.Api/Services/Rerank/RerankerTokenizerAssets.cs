using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BookmarkManager.Api.Services.Rerank;

/// <summary>Which side of the pair a template step contributes.</summary>
public enum PairStepKind
{
    SpecialToken,
    SequenceA,
    SequenceB
}

/// <summary>One step of tokenizer.json's <c>post_processor.pair</c> template, in order. A
/// <see cref="SpecialToken"/> step inserts a fixed id; <see cref="SequenceA"/>/<see cref="SequenceB"/>
/// splice in the query/passage subword ids. <see cref="TypeId"/> is whatever the template says (for
/// BERT-family tokenizers this is 0 for the query segment and 1 for the passage segment; for
/// RoBERTa/XLM-R-family tokenizers - including bge-reranker-base - it is 0 throughout, since those models
/// have no next-sentence-prediction head and don't use token_type_ids at all).</summary>
public sealed record PairTemplateStep(PairStepKind Kind, uint SpecialTokenId, int TypeId);

/// <summary>Special-token ids and pair-encoding template read out of a tokenizer.json's
/// <c>post_processor</c> - never hardcoded, since different tokenizer families assign different ids to
/// [CLS]/[SEP]-equivalents (BERT: 101/102; bge-reranker-base's XLM-RoBERTa tokenizer: &lt;s&gt;=0,
/// &lt;/s&gt;=2) and even different pair *shapes* (BERT: single SEP between segments and 0/1
/// token_type_ids; XLM-R: double SEP and all-zero token_type_ids, because the tokenizer.json's own "pair"
/// template says so). Getting this wrong silently produces plausible-looking garbage scores, so every
/// assumption here is read from the file and verified rather than guessed.</summary>
public sealed class RerankerTokenizerAssets
{
    /// <summary>Special-token ids <see cref="Tokenizers.DotNet.Tokenizer.Encode"/> prepends to a lone
    /// sequence per the "single" template - stripped back off before splicing our own pair together.</summary>
    public required IReadOnlyList<uint> LeadingSingleIds { get; init; }

    /// <summary>Special-token ids appended to a lone sequence per the "single" template.</summary>
    public required IReadOnlyList<uint> TrailingSingleIds { get; init; }

    /// <summary>The "pair" template, in order: how to interleave the query/passage ids with special tokens
    /// and what token_type_id each part gets.</summary>
    public required IReadOnlyList<PairTemplateStep> PairTemplate { get; init; }

    /// <summary>Padding token id, used to fill the batch tensor's short rows.</summary>
    public required uint PadId { get; init; }

    public static RerankerTokenizerAssets Load(string tokenizerJsonPath) =>
        Parse(File.ReadAllText(tokenizerJsonPath));

    public static RerankerTokenizerAssets Parse(string tokenizerJson)
    {
        using var doc = JsonDocument.Parse(tokenizerJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("post_processor", out var postProcessor) ||
            postProcessor.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "tokenizer.json has no post_processor; cannot determine special tokens for pair encoding.");
        }

        var type = postProcessor.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
        if (!string.Equals(type, "TemplateProcessing", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported post_processor type '{type}'; expected TemplateProcessing.");
        }

        var specialTokenIds = ReadSpecialTokenIds(postProcessor);

        var single = postProcessor.GetProperty("single");
        var (leading, trailing) = ReadSingleTemplate(single, specialTokenIds);

        var pair = postProcessor.GetProperty("pair");
        var pairTemplate = ReadPairTemplate(pair, specialTokenIds);

        var padId = ReadPadId(root);

        return new RerankerTokenizerAssets
        {
            LeadingSingleIds = leading,
            TrailingSingleIds = trailing,
            PairTemplate = pairTemplate,
            PadId = padId
        };
    }

    private static Dictionary<string, uint> ReadSpecialTokenIds(JsonElement postProcessor)
    {
        if (!postProcessor.TryGetProperty("special_tokens", out var specialTokens) ||
            specialTokens.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("tokenizer.json post_processor has no special_tokens map.");
        }

        var map = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var prop in specialTokens.EnumerateObject())
        {
            var ids = prop.Value.GetProperty("ids");
            var first = ids.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Number)
            {
                throw new InvalidOperationException(
                    $"tokenizer.json special_tokens['{prop.Name}'].ids is empty or non-numeric.");
            }
            map[prop.Name] = first.GetUInt32();
        }
        return map;
    }

    private static (IReadOnlyList<uint> Leading, IReadOnlyList<uint> Trailing) ReadSingleTemplate(
        JsonElement single, IReadOnlyDictionary<string, uint> specialTokenIds)
    {
        var leading = new List<uint>();
        var trailing = new List<uint>();
        var seenSequence = false;

        foreach (var step in single.EnumerateArray())
        {
            if (step.TryGetProperty("SpecialToken", out var special))
            {
                var id = ResolveSpecialTokenId(special, specialTokenIds, "post_processor.single");
                (seenSequence ? trailing : leading).Add(id);
            }
            else if (step.TryGetProperty("Sequence", out _))
            {
                seenSequence = true;
            }
        }

        return (leading, trailing);
    }

    private static IReadOnlyList<PairTemplateStep> ReadPairTemplate(
        JsonElement pair, IReadOnlyDictionary<string, uint> specialTokenIds)
    {
        var steps = new List<PairTemplateStep>();

        foreach (var step in pair.EnumerateArray())
        {
            if (step.TryGetProperty("SpecialToken", out var special))
            {
                var id = ResolveSpecialTokenId(special, specialTokenIds, "post_processor.pair");
                var typeId = special.TryGetProperty("type_id", out var t) ? t.GetInt32() : 0;
                steps.Add(new PairTemplateStep(PairStepKind.SpecialToken, id, typeId));
            }
            else if (step.TryGetProperty("Sequence", out var sequence))
            {
                var segment = sequence.GetProperty("id").GetString();
                var typeId = sequence.TryGetProperty("type_id", out var t) ? t.GetInt32() : 0;
                var kind = segment switch
                {
                    "A" => PairStepKind.SequenceA,
                    "B" => PairStepKind.SequenceB,
                    _ => throw new InvalidOperationException(
                        $"tokenizer.json post_processor.pair has unknown sequence id '{segment}'.")
                };
                steps.Add(new PairTemplateStep(kind, SpecialTokenId: 0, typeId));
            }
        }

        if (steps.All(s => s.Kind != PairStepKind.SequenceA) || steps.All(s => s.Kind != PairStepKind.SequenceB))
        {
            throw new InvalidOperationException(
                "tokenizer.json post_processor.pair template is missing sequence A or B.");
        }

        return steps;
    }

    private static uint ResolveSpecialTokenId(
        JsonElement specialTokenStep, IReadOnlyDictionary<string, uint> specialTokenIds, string context)
    {
        var content = specialTokenStep.GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"tokenizer.json {context} SpecialToken step has no id.");
        if (!specialTokenIds.TryGetValue(content, out var id))
        {
            throw new InvalidOperationException(
                $"tokenizer.json {context} references special token '{content}' not listed in special_tokens.");
        }
        return id;
    }

    // tokenizer.json's top-level "padding" section is commonly null (padding is a batching concern the
    // caller owns, not the tokenizer's), so the pad id has to come from the added_tokens list instead -
    // the one marked special whose content names it as the pad token, covering both the BERT convention
    // ("[PAD]") and the RoBERTa/XLM-R convention ("<pad>") without hardcoding either.
    private static uint ReadPadId(JsonElement root)
    {
        if (root.TryGetProperty("added_tokens", out var addedTokens) && addedTokens.ValueKind == JsonValueKind.Array)
        {
            foreach (var token in addedTokens.EnumerateArray())
            {
                var isSpecial = token.TryGetProperty("special", out var s) && s.ValueKind == JsonValueKind.True;
                var content = token.TryGetProperty("content", out var c) ? c.GetString() : null;
                if (isSpecial && content is not null && content.Contains("pad", StringComparison.OrdinalIgnoreCase))
                    return token.GetProperty("id").GetUInt32();
            }
        }

        throw new InvalidOperationException(
            "tokenizer.json has no recognizable pad token in added_tokens; cannot safely batch-pad reranker inputs.");
    }
}
