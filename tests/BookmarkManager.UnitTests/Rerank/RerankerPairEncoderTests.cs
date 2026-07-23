using System;
using System.Linq;
using BookmarkManager.Api.Services.Rerank;
using Xunit;

namespace BookmarkManager.UnitTests.Rerank;

/// <summary>Pure unit tests for the pair splicing logic against a BERT-shaped fixture (single SEP, 0/1
/// token_type_ids). Uses a fake encode delegate instead of a real tokenizer so these run everywhere
/// without a model/tokenizer.json on disk - <see cref="RerankerTokenizerAssetsTests"/> covers reading the
/// template from a real tokenizer.json.</summary>
public sealed class RerankerPairEncoderTests
{
    private const string BertFixtureJson = """
        {
          "added_tokens": [
            { "id": 0, "content": "[PAD]", "special": true },
            { "id": 101, "content": "[CLS]", "special": true },
            { "id": 102, "content": "[SEP]", "special": true }
          ],
          "post_processor": {
            "type": "TemplateProcessing",
            "single": [
              { "SpecialToken": { "id": "[CLS]", "type_id": 0 } },
              { "Sequence": { "id": "A", "type_id": 0 } },
              { "SpecialToken": { "id": "[SEP]", "type_id": 0 } }
            ],
            "pair": [
              { "SpecialToken": { "id": "[CLS]", "type_id": 0 } },
              { "Sequence": { "id": "A", "type_id": 0 } },
              { "SpecialToken": { "id": "[SEP]", "type_id": 0 } },
              { "Sequence": { "id": "B", "type_id": 1 } },
              { "SpecialToken": { "id": "[SEP]", "type_id": 1 } }
            ],
            "special_tokens": {
              "[CLS]": { "id": "[CLS]", "ids": [101], "tokens": ["[CLS]"] },
              "[SEP]": { "id": "[SEP]", "ids": [102], "tokens": ["[SEP]"] }
            }
          }
        }
        """;

    private static readonly RerankerTokenizerAssets Assets = RerankerTokenizerAssets.Parse(BertFixtureJson);

    // Mimics Tokenizer.Encode: wraps whatever "bare" subword ids the text maps to with [CLS]/[SEP], exactly
    // like the real Tokenizers.DotNet.Tokenizer does per the tokenizer.json post_processor (verified
    // empirically against the real bge-reranker-base tokenizer.json: Encode("hello world") =>
    // [0, 33600, 31, 8999, 2] - wrapped with the single-template's leading/trailing special tokens).
    private static uint[] FakeEncode(string text)
    {
        var bare = ToBareIds(text);
        return new uint[] { 101 }.Concat(bare).Concat(new uint[] { 102 }).ToArray();
    }

    private static uint[] ToBareIds(string text) =>
        string.IsNullOrEmpty(text) ? Array.Empty<uint>() : Enumerable.Range(0, text.Length).Select(i => (uint)(1000 + i)).ToArray();

    [Fact]
    public void StripSingleWrap_RemovesLeadingClsAndTrailingSep()
    {
        var encoded = FakeEncode("abc");
        var stripped = RerankerPairEncoder.StripSingleWrap(encoded, Assets);
        Assert.Equal(new uint[] { 1000, 1001, 1002 }, stripped);
    }

    [Fact]
    public void StripSingleWrap_ThrowsWhenLeadingTokenDoesNotMatch()
    {
        var corrupted = new uint[] { 999, 1000, 102 };
        var ex = Assert.Throws<InvalidOperationException>(() => RerankerPairEncoder.StripSingleWrap(corrupted, Assets));
        Assert.Contains("leading", ex.Message);
    }

    [Fact]
    public void Encode_BuildsClsQuerySepPassageSep_WithTypeIdsZeroForQueryOneForPassage()
    {
        var (ids, typeIds) = RerankerPairEncoder.Encode(FakeEncode, Assets, "ab", "xyz", maxSequenceLength: 512);

        // [CLS] q q [SEP] p p p [SEP]
        Assert.Equal(new uint[] { 101, 1000, 1001, 102, 1000, 1001, 1002, 102 }, ids);
        Assert.Equal(new long[] { 0, 0, 0, 0, 1, 1, 1, 1 }, typeIds);
    }

    [Fact]
    public void Encode_TruncatesPassageNotQueryWhenOverBudget()
    {
        // BuildPair overhead for this fixture is 3 special tokens (CLS + 2 SEP). maxSequenceLength=10,
        // query has 4 bare tokens => budget for passage = 10 - 3 - 4 = 3.
        var (ids, typeIds) = RerankerPairEncoder.Encode(FakeEncode, Assets, "abcd", "0123456789", maxSequenceLength: 10);

        Assert.Equal(10, ids.Length);
        Assert.Equal(10, typeIds.Length);

        // Query segment (all 4 tokens) must be fully present, untouched.
        Assert.Equal(new uint[] { 1000, 1001, 1002, 1003 }, ids.Skip(1).Take(4).ToArray());

        // Passage segment truncated to the first 3 of its 10 bare tokens (truncation drops the tail, not
        // arbitrary tokens).
        Assert.Equal(new uint[] { 1000, 1001, 1002 }, ids.Skip(6).Take(3).ToArray());
    }

    [Fact]
    public void Encode_EmptyPassage_ProducesEmptyPassageSegment()
    {
        var (ids, typeIds) = RerankerPairEncoder.Encode(FakeEncode, Assets, "ab", string.Empty, maxSequenceLength: 512);

        // [CLS] q q [SEP] [SEP] - empty passage segment, still both SEPs present.
        Assert.Equal(new uint[] { 101, 1000, 1001, 102, 102 }, ids);
        Assert.Equal(new long[] { 0, 0, 0, 0, 1 }, typeIds);
    }
}
