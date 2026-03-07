using Microsoft.ML.Tokenizers;

namespace ManagedCode.MCPGateway;

internal static class McpGatewaySearchTokenizerFactory
{
    private const string ChatGptTokenizerModelName = "gpt-4o";

    private static readonly Lazy<Tokenizer> ChatGptTokenizer = new(
        static () => TiktokenTokenizer.CreateForModel(ChatGptTokenizerModelName),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static Tokenizer GetTokenizer() => ChatGptTokenizer.Value;
}
