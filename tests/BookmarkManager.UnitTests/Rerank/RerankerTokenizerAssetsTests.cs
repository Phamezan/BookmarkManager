using System;
using System.Linq;
using BookmarkManager.Api.Services.Rerank;
using Xunit;

namespace BookmarkManager.UnitTests.Rerank;

/// <summary>Verifies special-token ids and the pair template are read from tokenizer.json rather than
/// hardcoded, against two real tokenizer.json shapes: a BERT-family fixture (single SEP, 0/1
/// token_type_ids - the shape most cross-encoders use) and bge-reranker-base's actual XLM-RoBERTa
/// post_processor/added_tokens (double SEP, all-zero token_type_ids, ids 0/1/2 rather than BERT's
/// 101/102/103 - proving the spec's "verify from the file, don't guess" warning was warranted).</summary>
public sealed class RerankerTokenizerAssetsTests
{
    // A plausible real BERT-family tokenizer.json post_processor (added_tokens ordering intentionally not
    // 0/1/2/3 - the point is that ids are resolved by content lookup, not position).
    private const string BertFixtureJson = """
        {
          "added_tokens": [
            { "id": 0, "content": "[PAD]", "special": true },
            { "id": 100, "content": "[UNK]", "special": true },
            { "id": 101, "content": "[CLS]", "special": true },
            { "id": 102, "content": "[SEP]", "special": true },
            { "id": 103, "content": "[MASK]", "special": true }
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

    // Trimmed from the real Xenova/bge-reranker-base tokenizer.json (vocab/model section stripped - only
    // the metadata RerankerTokenizerAssets reads is kept). Confirmed against the actual downloaded file
    // and against Tokenizer.Encode("hello world") => [0, 33600, 31, 8999, 2].
    private const string BgeRerankerBaseFixtureJson = """
        {
          "added_tokens": [
            { "id": 0, "content": "<s>", "special": true },
            { "id": 1, "content": "<pad>", "special": true },
            { "id": 2, "content": "</s>", "special": true },
            { "id": 3, "content": "<unk>", "special": true },
            { "id": 250001, "content": "<mask>", "special": true }
          ],
          "post_processor": {
            "type": "TemplateProcessing",
            "single": [
              { "SpecialToken": { "id": "<s>", "type_id": 0 } },
              { "Sequence": { "id": "A", "type_id": 0 } },
              { "SpecialToken": { "id": "</s>", "type_id": 0 } }
            ],
            "pair": [
              { "SpecialToken": { "id": "<s>", "type_id": 0 } },
              { "Sequence": { "id": "A", "type_id": 0 } },
              { "SpecialToken": { "id": "</s>", "type_id": 0 } },
              { "SpecialToken": { "id": "</s>", "type_id": 0 } },
              { "Sequence": { "id": "B", "type_id": 0 } },
              { "SpecialToken": { "id": "</s>", "type_id": 0 } }
            ],
            "special_tokens": {
              "</s>": { "id": "</s>", "ids": [2], "tokens": ["</s>"] },
              "<s>": { "id": "<s>", "ids": [0], "tokens": ["<s>"] }
            }
          }
        }
        """;

    [Fact]
    public void Parse_BertFixture_ReadsClsSepIdsNotHardcoded()
    {
        var assets = RerankerTokenizerAssets.Parse(BertFixtureJson);

        Assert.Equal(new uint[] { 101 }, assets.LeadingSingleIds);
        Assert.Equal(new uint[] { 102 }, assets.TrailingSingleIds);
        Assert.Equal(0u, assets.PadId);
    }

    [Fact]
    public void Parse_BertFixture_PairTemplateHasZeroForQueryAndOneForPassage()
    {
        var assets = RerankerTokenizerAssets.Parse(BertFixtureJson);

        var typeIds = assets.PairTemplate.Select(s => (s.Kind, s.TypeId)).ToList();
        Assert.Equal(
            new[]
            {
                (PairStepKind.SpecialToken, 0), // [CLS]
                (PairStepKind.SequenceA, 0),    // query
                (PairStepKind.SpecialToken, 0), // [SEP] after query
                (PairStepKind.SequenceB, 1),    // passage
                (PairStepKind.SpecialToken, 1)  // [SEP] after passage
            },
            typeIds);
    }

    [Fact]
    public void Parse_BgeRerankerBaseFixture_ReadsRoBertaSpecialTokenIds()
    {
        // The real model's special tokens are <s>=0 and </s>=2, NOT BERT's conventional 101/102 - this is
        // exactly the trap the task spec warned about ("commonly 0/101/102/103, but verify from the file").
        var assets = RerankerTokenizerAssets.Parse(BgeRerankerBaseFixtureJson);

        Assert.Equal(new uint[] { 0 }, assets.LeadingSingleIds);
        Assert.Equal(new uint[] { 2 }, assets.TrailingSingleIds);
        Assert.Equal(1u, assets.PadId);
    }

    [Fact]
    public void Parse_BgeRerankerBaseFixture_PairTemplateIsDoubleSepAllZeroTypeIds()
    {
        // XLM-RoBERTa has no next-sentence-prediction head, so its pair template uses a double </s>
        // separator and all-zero token_type_ids - unlike BERT's single SEP + 0/1 split. Verified against
        // the real downloaded tokenizer.json.
        var assets = RerankerTokenizerAssets.Parse(BgeRerankerBaseFixtureJson);

        var kinds = assets.PairTemplate.Select(s => s.Kind).ToList();
        Assert.Equal(
            new[]
            {
                PairStepKind.SpecialToken,  // <s>
                PairStepKind.SequenceA,     // query
                PairStepKind.SpecialToken,  // </s>
                PairStepKind.SpecialToken,  // </s> (doubled)
                PairStepKind.SequenceB,     // passage
                PairStepKind.SpecialToken   // </s>
            },
            kinds);

        Assert.All(assets.PairTemplate, s => Assert.Equal(0, s.TypeId));
    }

    [Fact]
    public void Parse_MissingPostProcessor_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RerankerTokenizerAssets.Parse("{}"));
        Assert.Contains("post_processor", ex.Message);
    }

    [Fact]
    public void Parse_UnsupportedPostProcessorType_Throws()
    {
        const string json = """{ "post_processor": { "type": "ByteLevel" } }""";
        var ex = Assert.Throws<InvalidOperationException>(() => RerankerTokenizerAssets.Parse(json));
        Assert.Contains("TemplateProcessing", ex.Message);
    }

    [Fact]
    public void Parse_NoRecognizablePadToken_Throws()
    {
        const string json = """
            {
              "added_tokens": [ { "id": 101, "content": "[CLS]", "special": true } ],
              "post_processor": {
                "type": "TemplateProcessing",
                "single": [
                  { "SpecialToken": { "id": "[CLS]", "type_id": 0 } },
                  { "Sequence": { "id": "A", "type_id": 0 } }
                ],
                "pair": [
                  { "SpecialToken": { "id": "[CLS]", "type_id": 0 } },
                  { "Sequence": { "id": "A", "type_id": 0 } },
                  { "Sequence": { "id": "B", "type_id": 0 } }
                ],
                "special_tokens": {
                  "[CLS]": { "id": "[CLS]", "ids": [101], "tokens": ["[CLS]"] }
                }
              }
            }
            """;
        var ex = Assert.Throws<InvalidOperationException>(() => RerankerTokenizerAssets.Parse(json));
        Assert.Contains("pad", ex.Message);
    }
}
