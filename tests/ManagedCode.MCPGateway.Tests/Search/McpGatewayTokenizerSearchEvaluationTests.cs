using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewayTokenizerSearchEvaluationTests
{
    [TUnit.Core.Test]
    public async Task SearchAsync_TokenizerSearchMeetsEvaluationThresholds()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            TokenizerSearchTestSupport.UseTokenizerSearch(options);
            RegisterEvaluationTools(options);
        });
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();
        var highRelevanceMetrics = await EvaluateMatchBucketAsync(
            gateway,
            "high-relevance",
            HighRelevanceQueries);
        var borderlineMetrics = await EvaluateMatchBucketAsync(
            gateway,
            "borderline",
            BorderlineQueries);
        var multilingualMetrics = await EvaluateMatchBucketAsync(
            gateway,
            "multilingual",
            MultilingualQueries);
        var typoMetrics = await EvaluateMatchBucketAsync(
            gateway,
            "typo",
            TypoQueries);
        var weakIntentMetrics = await EvaluateMatchBucketAsync(
            gateway,
            "weak-intent",
            WeakIntentQueries);
        var irrelevantMetrics = await EvaluateIrrelevantBucketAsync(gateway);

        Console.WriteLine(
            $"ChatGptO200kBase / high-relevance: top1={highRelevanceMetrics.Top1Accuracy:P2}; top3={highRelevanceMetrics.Top3Accuracy:P2}; top5={highRelevanceMetrics.Top5Accuracy:P2}; mrr={highRelevanceMetrics.MeanReciprocalRank:F2}");
        Console.WriteLine(
            $"ChatGptO200kBase / borderline: top1={borderlineMetrics.Top1Accuracy:P2}; top3={borderlineMetrics.Top3Accuracy:P2}; top5={borderlineMetrics.Top5Accuracy:P2}; mrr={borderlineMetrics.MeanReciprocalRank:F2}");
        Console.WriteLine(
            $"ChatGptO200kBase / multilingual: top1={multilingualMetrics.Top1Accuracy:P2}; top3={multilingualMetrics.Top3Accuracy:P2}; top5={multilingualMetrics.Top5Accuracy:P2}; mrr={multilingualMetrics.MeanReciprocalRank:F2}");
        Console.WriteLine(
            $"ChatGptO200kBase / typo: top1={typoMetrics.Top1Accuracy:P2}; top3={typoMetrics.Top3Accuracy:P2}; top5={typoMetrics.Top5Accuracy:P2}; mrr={typoMetrics.MeanReciprocalRank:F2}");
        Console.WriteLine(
            $"ChatGptO200kBase / weak-intent: top1={weakIntentMetrics.Top1Accuracy:P2}; top3={weakIntentMetrics.Top3Accuracy:P2}; top5={weakIntentMetrics.Top5Accuracy:P2}; mrr={weakIntentMetrics.MeanReciprocalRank:F2}");
        Console.WriteLine(
            $"ChatGptO200kBase / irrelevant: low-confidence={irrelevantMetrics.LowConfidenceRate:P2}; avg-top-score={irrelevantMetrics.AverageTopScore:F2}");

        await Assert.That(buildResult.ToolCount).IsEqualTo(50);
        await Assert.That(highRelevanceMetrics.Top1Accuracy >= 0.82d).IsTrue();
        await Assert.That(highRelevanceMetrics.Top3Accuracy >= 0.95d).IsTrue();
        await Assert.That(highRelevanceMetrics.Top5Accuracy >= 0.95d).IsTrue();
        await Assert.That(highRelevanceMetrics.MeanReciprocalRank >= 0.90d).IsTrue();
        await Assert.That(borderlineMetrics.Top3Accuracy >= 0.80d).IsTrue();
        await Assert.That(borderlineMetrics.Top5Accuracy >= 0.95d).IsTrue();
        await Assert.That(multilingualMetrics.Top3Accuracy >= 0.85d).IsTrue();
        await Assert.That(multilingualMetrics.Top5Accuracy >= 0.95d).IsTrue();
        await Assert.That(typoMetrics.Top3Accuracy >= 0.85d).IsTrue();
        await Assert.That(typoMetrics.Top5Accuracy >= 0.95d).IsTrue();
        await Assert.That(weakIntentMetrics.Top3Accuracy >= 0.85d).IsTrue();
        await Assert.That(weakIntentMetrics.Top5Accuracy >= 0.88d).IsTrue();
        await Assert.That(irrelevantMetrics.LowConfidenceRate >= 0.95d).IsTrue();
        await Assert.That(irrelevantMetrics.AverageTopScore <= 0.15d).IsTrue();
    }
}
