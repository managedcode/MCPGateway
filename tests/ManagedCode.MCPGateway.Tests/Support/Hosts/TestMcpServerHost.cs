using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class TestMcpServerHost(
    ServiceProvider serviceProvider,
    McpClient client,
    ModelContextProtocol.Server.McpServer server,
    CancellationTokenSource cancellationTokenSource,
    Task serverTask,
    IReadOnlyList<JsonObject> capturedMeta
) : IAsyncDisposable
{
    public McpClient Client { get; } = client;

    public IReadOnlyList<JsonObject> CapturedMeta { get; } = capturedMeta;

    public static async Task<TestMcpServerHost> StartAsync(
        CancellationToken cancellationToken = default
    ) =>
        await StartAsync(
            static builder =>
                builder
                    .WithTools<TestMcpTools>()
                    .WithPrompts<TestMcpPrompts>()
                    .WithResources<TestMcpResources>(),
            cancellationToken
        );

    public static async Task<TestMcpServerHost> StartGraphAsync(
        CancellationToken cancellationToken = default
    ) =>
        await StartAsync(
            static builder =>
                builder
                    .WithTools<TestMcpGraphTools>()
                    .WithPrompts<TestMcpGraphPrompts>()
                    .WithResources<TestMcpGraphResources>(),
            cancellationToken
        );

    public static async Task<TestMcpServerHost> StartOperationsAsync(
        CancellationToken cancellationToken = default
    ) =>
        await StartAsync(
            static builder =>
                builder
                    .WithTools<TestMcpOperationsTools>()
                    .WithPrompts<TestMcpOperationsPrompts>()
                    .WithResources<TestMcpOperationsResources>(),
            cancellationToken
        );

    private static async Task<TestMcpServerHost> StartAsync(
        Action<IMcpServerBuilder> configureTools,
        CancellationToken cancellationToken
    )
    {
        var capturedMeta = new List<JsonObject>();
        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));
        configureTools(services.AddMcpServer());

        var serviceProvider = services.BuildServiceProvider();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var options = serviceProvider.GetRequiredService<IOptions<McpServerOptions>>();

        options.Value.Filters.Request.CallToolFilters.Add(next =>
            async (request, token) =>
            {
                if (request.Params?.Meta is JsonObject meta)
                {
                    capturedMeta.Add((JsonObject)meta.DeepClone());
                }
                else if (
                    request.Params?.Meta is not null
                    && JsonSerializer.SerializeToNode(request.Params.Meta)
                        is JsonObject serializedMeta
                )
                {
                    capturedMeta.Add(serializedMeta);
                }

                return await next(request, token);
            }
        );

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream()
        );

        var server = ModelContextProtocol.Server.McpServer.Create(
            serverTransport,
            options.Value,
            loggerFactory,
            serviceProvider
        );

        var serverCancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(serverCancellation.Token);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream(),
            loggerFactory
        );

        var client = await McpClient.CreateAsync(
            clientTransport,
            new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = "managedcode-mcpgateway-tests",
                    Version = "1.0.0",
                },
            },
            loggerFactory,
            cancellationToken
        );

        return new TestMcpServerHost(
            serviceProvider,
            client,
            server,
            serverCancellation,
            serverTask,
            capturedMeta
        );
    }

    public async ValueTask DisposeAsync()
    {
        cancellationTokenSource.Cancel();

        await McpTestServerShutdown.AwaitServerStopAsync(
            serverTask,
            cancellationTokenSource.Token
        );

        await Client.DisposeAsync();
        await server.DisposeAsync();
        cancellationTokenSource.Dispose();
        await serviceProvider.DisposeAsync();
    }

    [McpServerToolType]
    private sealed class TestMcpTools
    {
        [McpServerTool(
            Name = "github_repository_search",
            Title = "Search GitHub repositories",
            Idempotent = true,
            ReadOnly = true,
            UseStructuredContent = true
        )]
        [McpMeta("vendor", "upstream")]
        [Description("Search GitHub repositories by query text.")]
        public static TestMcpSearchResult SearchRepositories(
            [Description("Repository search query.")] string query
        ) => new(query, "mcp");

        [McpServerTool(
            Name = "json_text_search",
            Title = "Return JSON as text",
            Idempotent = true,
            ReadOnly = true,
            UseStructuredContent = false
        )]
        [Description("Return a JSON document as plain text content.")]
        public static string ReturnJsonAsText([Description("Payload query text.")] string query) =>
            JsonSerializer.Serialize(new TestMcpSearchResult(query, "text-json"));

        [McpServerTool(
            Name = "plain_text_search",
            Title = "Return plain text",
            Idempotent = true,
            ReadOnly = true,
            UseStructuredContent = false
        )]
        [Description("Return plain text content.")]
        public static string ReturnPlainText([Description("Payload query text.")] string query) =>
            $"plain:{query}";
    }

    private sealed class TestMcpGraphTools
    {
        [McpServerTool(
            Name = "story_item_search",
            Title = "Search story feed items",
            Idempotent = true,
            ReadOnly = true,
            UseStructuredContent = true
        )]
        [Description("Search story feed items by query text before detail lookup or comments.")]
        public static TestMcpSearchResult SearchStoryItems(
            [Description("Story feed search query.")] string query
        ) => new(query, "story-search");

        [McpServerTool(
            Name = "story_item_detail",
            Title = "Read story feed item detail",
            Idempotent = true,
            ReadOnly = true,
            UseStructuredContent = true
        )]
        [Description("Read story feed item detail by story id after search resolves the item.")]
        public static TestMcpSearchResult ReadStoryItem(
            [Description("Story item id.")] string storyId
        ) => new(storyId, "story-detail");

        [McpServerTool(
            Name = "story_comments_list",
            Title = "List story comments",
            Idempotent = true,
            ReadOnly = true,
            UseStructuredContent = true
        )]
        [Description("List story comments after a story item has been found.")]
        public static TestMcpSearchResult ListStoryComments(
            [Description("Story item id.")] string storyId
        ) => new(storyId, "story-comments");

        [McpServerTool(
            Name = "people_profile_search",
            Title = "Search people profiles",
            Idempotent = true,
            ReadOnly = true,
            UseStructuredContent = true
        )]
        [Description("Search people profiles by name, relationship, or organization.")]
        public static TestMcpSearchResult SearchPeople(
            [Description("People profile search query.")] string query
        ) => new(query, "people-search");
    }

    [McpServerPromptType]
    private sealed class TestMcpGraphPrompts
    {
        [McpServerPrompt(Name = "story_triage_system_prompt", Title = "Story triage")]
        [Description("Builds a story triage prompt for a story feed item.")]
        public static GetPromptResult BuildStoryTriagePrompt(
            [Description("Story title.")] string storyTitle
        ) =>
            new()
            {
                Description = "Story triage system prompt.",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock
                        {
                            Text = $"Review story '{storyTitle}' and summarize key risks.",
                        },
                    },
                ],
            };
    }

    [McpServerToolType]
    private sealed class TestMcpOperationsTools
    {
        [McpServerTool(
            Name = "incident_status_lookup",
            Title = "Lookup incident status",
            Idempotent = true,
            ReadOnly = true,
            UseStructuredContent = true
        )]
        [Description("Lookup incident status by incident identifier.")]
        public static TestMcpSearchResult LookupIncidentStatus(
            [Description("Incident identifier.")] string incidentId
        ) => new(incidentId, "incident-status");

        [McpServerTool(
            Name = "deployment_status_lookup",
            Title = "Lookup deployment status",
            Idempotent = true,
            ReadOnly = true,
            UseStructuredContent = true
        )]
        [Description("Lookup deployment status by environment name.")]
        public static TestMcpSearchResult LookupDeploymentStatus(
            [Description("Environment name.")] string environment
        ) => new(environment, "deployment-status");
    }

    [McpServerPromptType]
    private sealed class TestMcpPrompts
    {
        [McpServerPrompt(Name = "repository_triage_system_prompt", Title = "Repository triage")]
        [Description("Builds a repository triage system prompt for the selected repository.")]
        public static GetPromptResult BuildRepositoryTriagePrompt(
            [Description("Repository name.")] string repository,
            [Description("Preferred locale.")] string locale = "en"
        ) =>
            new()
            {
                Description = "Repository triage system prompt.",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock
                        {
                            Text = $"Review repository '{repository}' using locale '{locale}'.",
                        },
                    },
                ],
            };

        [McpServerPrompt(Name = "incident_response_system_prompt", Title = "Incident response")]
        [Description("Builds an incident response system prompt for an active incident.")]
        public static GetPromptResult BuildIncidentResponsePrompt(
            [Description("Incident title.")] string incidentTitle
        ) =>
            new()
            {
                Description = "Incident response system prompt.",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock
                        {
                            Text = $"Prepare an incident response plan for '{incidentTitle}'.",
                        },
                    },
                ],
            };
    }

    [McpServerResourceType]
    private sealed class TestMcpResources
    {
        [McpServerResource(
            UriTemplate = "docs://repository/overview",
            Name = "repository_overview",
            Title = "Repository overview",
            MimeType = "text/markdown"
        )]
        [Description("Returns repository overview markdown.")]
        public static TextResourceContents GetRepositoryOverview() =>
            new()
            {
                Uri = "docs://repository/overview",
                MimeType = "text/markdown",
                Text =
                    "# ManagedCode.MCPGateway\n\nThis repository aggregates MCP tools, prompts, and resources.",
            };

        [McpServerResource(
            UriTemplate = "docs://repository/archive",
            Name = "repository_archive",
            Title = "Repository archive",
            MimeType = "application/octet-stream"
        )]
        [Description("Returns a small binary repository archive sample.")]
        public static BlobResourceContents GetRepositoryArchive() =>
            BlobResourceContents.FromBytes(
                new byte[] { 1, 2, 3, 4 },
                "docs://repository/archive",
                "application/octet-stream"
            );

        [McpServerResource(
            UriTemplate = "docs://issues/{id}",
            Name = "issue_detail",
            Title = "Issue detail",
            MimeType = "application/json"
        )]
        [Description("Returns issue detail by issue identifier.")]
        public static TextResourceContents GetIssueDetail(
            [Description("Issue identifier.")] string id
        ) =>
            new()
            {
                Uri = $"docs://issues/{id}",
                MimeType = "application/json",
                Text = JsonSerializer.Serialize(
                    new
                    {
                        id,
                        status = "open",
                        area = "gateway-runtime",
                    }
                ),
            };
    }

    [McpServerPromptType]
    private sealed class TestMcpOperationsPrompts
    {
        [McpServerPrompt(Name = "deployment_review_system_prompt", Title = "Deployment review")]
        [Description("Builds a deployment review system prompt for a target environment.")]
        public static GetPromptResult BuildDeploymentReviewPrompt(
            [Description("Target environment.")] string environment
        ) =>
            new()
            {
                Description = "Deployment review system prompt.",
                Messages =
                [
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock
                        {
                            Text = $"Review the deployment readiness for '{environment}'.",
                        },
                    },
                ],
            };
    }

    [McpServerResourceType]
    private sealed class TestMcpGraphResources
    {
        [McpServerResource(
            UriTemplate = "graph://stories/{storyId}",
            Name = "story_context",
            Title = "Story context",
            MimeType = "text/markdown"
        )]
        [Description("Returns graph-backed story context for a story item.")]
        public static TextResourceContents GetStoryContext(
            [Description("Story identifier.")] string storyId
        ) =>
            new()
            {
                Uri = $"graph://stories/{storyId}",
                MimeType = "text/markdown",
                Text = $"# Story {storyId}\n\nRelated nodes: comments, owner, deployment.",
            };
    }

    [McpServerResourceType]
    private sealed class TestMcpOperationsResources
    {
        [McpServerResource(
            UriTemplate = "ops://deployments/summary",
            Name = "deployment_summary",
            Title = "Deployment summary",
            MimeType = "application/json"
        )]
        [Description("Returns a deployment summary resource.")]
        public static TextResourceContents GetDeploymentSummary() =>
            new()
            {
                Uri = "ops://deployments/summary",
                MimeType = "application/json",
                Text = JsonSerializer.Serialize(
                    new
                    {
                        active = 3,
                        healthy = true,
                    }
                ),
            };

        [McpServerResource(
            UriTemplate = "ops://runbooks/{environment}",
            Name = "runbook_detail",
            Title = "Runbook detail",
            MimeType = "text/markdown"
        )]
        [Description("Returns the runbook for a deployment environment.")]
        public static TextResourceContents GetRunbook(
            [Description("Deployment environment.")] string environment
        ) =>
            new()
            {
                Uri = $"ops://runbooks/{environment}",
                MimeType = "text/markdown",
                Text =
                    $"# Runbook for {environment}\n\n1. Validate health checks.\n2. Confirm rollback target.",
            };
    }

    private sealed record TestMcpSearchResult(string Query, string Source);
}
