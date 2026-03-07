using Microsoft.ML.Tokenizers;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayTokenSearchTokenizerFactory
{
    private const string ChatGptTokenizerModelName = "gpt-4o";
    private const string Gpt2BpeEncodingName = "r50k_base";

    private static readonly Lazy<Tokenizer> ChatGptTokenizer = new(
        static () => TiktokenTokenizer.CreateForModel(ChatGptTokenizerModelName),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<Tokenizer> Gpt2BpeTokenizer = new(
        static () => TiktokenTokenizer.CreateForEncoding(Gpt2BpeEncodingName),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static Tokenizer GetTokenizer(McpGatewayTokenSearchTokenizer tokenizer)
        => tokenizer switch
        {
            McpGatewayTokenSearchTokenizer.Gpt2Bpe => Gpt2BpeTokenizer.Value,
            _ => ChatGptTokenizer.Value
        };
}
