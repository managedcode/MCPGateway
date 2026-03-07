using ManagedCode.MCPGateway.Abstractions;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewayTokenizerSearchEvaluationTests
{
    private static void RegisterEvaluationTools(McpGatewayOptions options)
    {
        foreach (var tool in EvaluationTools)
        {
            options.AddTool(
                "local",
                TestFunctionFactory.CreateFunction(tool.Callback, tool.Name, tool.Description));
        }
    }

    private static async Task<EvaluationMetrics> EvaluateMatchBucketAsync(
        IMcpGateway gateway,
        string bucketName,
        IReadOnlyList<EvaluationQuerySpec> evaluationQueries)
    {
        var top1Hits = 0;
        var top3Hits = 0;
        var top5Hits = 0;
        var reciprocalRankSum = 0d;

        foreach (var evaluationQuery in evaluationQueries)
        {
            var searchResult = await gateway.SearchAsync(evaluationQuery.Query);
            var expectedToolIds = evaluationQuery.AcceptableToolNames
                .Select(static toolName => $"local:{toolName}")
                .ToHashSet(StringComparer.Ordinal);

            if (searchResult.Matches.Count > 0 &&
                expectedToolIds.Contains(searchResult.Matches[0].ToolId))
            {
                top1Hits++;
            }

            var rank = searchResult.Matches
                .Select((match, index) => new { match.ToolId, Rank = index + 1 })
                .FirstOrDefault(item => expectedToolIds.Contains(item.ToolId))
                ?.Rank;

            if (rank is int value)
            {
                if (value <= 3)
                {
                    top3Hits++;
                }

                if (value <= 5)
                {
                    top5Hits++;
                }

                reciprocalRankSum += 1d / value;
            }
            else
            {
                Console.WriteLine(
                    $"MISS {bucketName}: '{evaluationQuery.Query}' expected [{string.Join(", ", expectedToolIds)}] but got [{string.Join(", ", searchResult.Matches.Select(static match => match.ToolId))}]");
            }
        }

        return new EvaluationMetrics(
            Top1Accuracy: (double)top1Hits / evaluationQueries.Count,
            Top3Accuracy: (double)top3Hits / evaluationQueries.Count,
            Top5Accuracy: (double)top5Hits / evaluationQueries.Count,
            MeanReciprocalRank: reciprocalRankSum / evaluationQueries.Count);
    }

    private static async Task<IrrelevantMetrics> EvaluateIrrelevantBucketAsync(IMcpGateway gateway)
    {
        var lowConfidenceHits = 0;
        var topScoreSum = 0d;

        foreach (var query in IrrelevantQueries)
        {
            var searchResult = await gateway.SearchAsync(query);
            var topScore = searchResult.Matches.Count > 0
                ? searchResult.Matches[0].Score
                : 0d;
            topScoreSum += topScore;

            if (topScore <= 0.20d)
            {
                lowConfidenceHits++;
            }
            else
            {
                Console.WriteLine(
                    $"IRRELEVANT HIGH SCORE: '{query}' returned {topScore:F2} for [{string.Join(", ", searchResult.Matches.Select(static match => match.ToolId))}]");
            }
        }

        return new IrrelevantMetrics(
            LowConfidenceRate: (double)lowConfidenceHits / IrrelevantQueries.Length,
            AverageTopScore: topScoreSum / IrrelevantQueries.Length);
    }

    private static EvaluationToolSpec[] CreateSpecs(
        Delegate callback,
        params (string Name, string Description)[] definitions)
        => definitions
            .Select(definition => new EvaluationToolSpec(definition.Name, definition.Description, callback))
            .ToArray();

    private sealed record EvaluationToolSpec(string Name, string Description, Delegate Callback);

    private sealed record EvaluationQuerySpec(string Query, params string[] AcceptableToolNames);

    private sealed record EvaluationMetrics(double Top1Accuracy, double Top3Accuracy, double Top5Accuracy, double MeanReciprocalRank);

    private sealed record IrrelevantMetrics(double LowConfidenceRate, double AverageTopScore);

}
