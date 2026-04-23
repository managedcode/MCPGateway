using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class MultiGatewayHttpHost(
    WebApplication application,
    ILoggerFactory loggerFactory,
    IReadOnlyList<MultiGatewayHttpHost.HostedGatewayRoute> routes
) : IAsyncDisposable
{
    public const string SharedToolName = "resolve_gateway_identity";
    public const string ExportedSharedToolName = $"local:{SharedToolName}";
    private const string AuthenticationScheme = "TestGatewayBearer";
    private const string GatewayClaimType = "mcp-gateway";

    public IReadOnlyList<HostedGatewayRoute> Routes { get; } = routes;

    public static async Task<MultiGatewayHttpHost> StartAsync(
        CancellationToken cancellationToken = default
    )
    {
        var routes = CreateRoutes();
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IReadOnlyList<HostedGatewayRoute>>(routes);
        builder
            .Services.AddAuthentication(AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, TestGatewayAuthenticationHandler>(
                AuthenticationScheme,
                static _ => { }
            );
        builder.Services.AddAuthorization(options =>
        {
            foreach (var route in routes)
            {
                options.AddPolicy(
                    route.PolicyName,
                    policy =>
                        policy.RequireAuthenticatedUser().RequireClaim(
                            GatewayClaimType,
                            route.GatewayId
                        )
                );
            }
        });
        builder.Services.AddMcpGateway(static _ => { });
        builder.Services.AddSingleton<IMcpGatewayServerBindingResolver, RouteAwareGatewayBindingResolver>();
        builder
            .Services.AddMcpServer()
            .WithMcpGatewayCatalog()
            .WithHttpTransport(static _ => { })
            .AddAuthorizationFilters();

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();

        foreach (var route in routes)
        {
            application.MapMcp(route.RoutePattern).RequireAuthorization(route.PolicyName);
        }

        await application.StartAsync(cancellationToken);

        return new MultiGatewayHttpHost(
            application,
            application.Services.GetRequiredService<ILoggerFactory>(),
            routes
        );
    }

    public HostedGatewayRoute GetRoute(string gatewayId) =>
        Routes.Single(route => string.Equals(route.GatewayId, gatewayId, StringComparison.Ordinal));

    public HttpClient CreateHttpClient(
        string gatewayId,
        string? bearerToken = null,
        bool includeAuthorizationHeader = true
    )
    {
        var route = GetRoute(gatewayId);
        var client = application.GetTestClient();

        if (includeAuthorizationHeader)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                bearerToken ?? route.Token
            );
        }

        return client;
    }

    public async Task<McpClient> CreateClientAsync(
        string gatewayId,
        string? bearerToken = null,
        CancellationToken cancellationToken = default
    )
    {
        var route = GetRoute(gatewayId);
        var httpClient = CreateHttpClient(gatewayId, bearerToken);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, route.RoutePattern),
                Name = route.GatewayId,
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            httpClient,
            loggerFactory,
            true
        );

        try
        {
            return await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ClientInfo = new Implementation
                    {
                        Name = "managedcode-mcpgateway-http-tests",
                        Version = "1.0.0",
                    },
                },
                loggerFactory,
                cancellationToken
            );
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await application.StopAsync();
        await application.DisposeAsync();
    }

    private static HostedGatewayRoute[] CreateRoutes() =>
    [
        new("gateway-1", "/mcp/gateway-1", "gateway-1-policy", "gateway-1-token"),
        new("gateway-2", "/mcp/gateway-2", "gateway-2-policy", "gateway-2-token"),
        new("gateway-3", "/mcp/gateway-3", "gateway-3-policy", "gateway-3-token"),
        new("gateway-4", "/mcp/gateway-4", "gateway-4-policy", "gateway-4-token"),
        new("gateway-5", "/mcp/gateway-5", "gateway-5-policy", "gateway-5-token"),
    ];

    private static IReadOnlyList<IMcpGatewayServerSource> CreateSources(IMcpGatewayRegistry registry) =>
        ((IMcpGatewayCatalogSource)registry)
            .CreateSnapshot()
            .Registrations.Select(static registration =>
                (IMcpGatewayServerSource)new McpGatewayRegistrationBoundServerSource(registration)
            )
            .ToList();

    internal sealed record HostedGatewayRoute(
        string GatewayId,
        string RoutePattern,
        string PolicyName,
        string Token
    );

    private sealed class RouteAwareGatewayBindingResolver(
        IMcpGatewayFactory gatewayFactory,
        IHttpContextAccessor httpContextAccessor,
        IReadOnlyList<HostedGatewayRoute> routes
    ) : IMcpGatewayServerBindingResolver
    {
        public ValueTask<IMcpGatewayServerBinding> ResolveAsync(
            IServiceProvider? requestServices,
            IServiceProvider serverServices,
            ModelContextProtocol.Server.McpServer server,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            var httpContext =
                httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException(
                    "HTTP context is required for route-aware MCP gateway binding."
                );
            var route = ResolveRoute(httpContext.Request.Path);
            var gatewayInstance = gatewayFactory.Create(options =>
            {
                options.AddTool(
                    "local",
                    TestFunctionFactory.CreateFunction(
                        (string query) => $"{route.GatewayId}:{query}",
                        SharedToolName,
                        "Return the gateway identity for HTTP route isolation tests."
                    )
                );
            });
            var sources = CreateSources(gatewayInstance.Registry);

            return ValueTask.FromResult<IMcpGatewayServerBinding>(
                new McpGatewayServerBinding(
                    gatewayInstance.Gateway,
                    gatewayInstance.PromptCatalog,
                    gatewayInstance.ResourceCatalog,
                    gatewayInstance.Registry,
                    listSourcesAsync: _ => ValueTask.FromResult(sources),
                    disposeAsync: gatewayInstance.DisposeAsync
                )
            );
        }

        private HostedGatewayRoute ResolveRoute(PathString requestPath)
        {
            foreach (var route in routes)
            {
                if (requestPath.StartsWithSegments(route.RoutePattern))
                {
                    return route;
                }
            }

            throw new InvalidOperationException(
                $"No hosted gateway route matches request path '{requestPath}'."
            );
        }
    }

    private sealed class TestGatewayAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        IReadOnlyList<HostedGatewayRoute> routes
    ) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var token = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                return Task.FromResult(
                    AuthenticateResult.Fail("A bearer token is required for MCP HTTP tests.")
                );
            }

            var route = routes.SingleOrDefault(candidate =>
                string.Equals(candidate.Token, token, StringComparison.Ordinal)
            );
            if (route is null)
            {
                return Task.FromResult(AuthenticateResult.Fail("The bearer token is invalid."));
            }

            var claims = new[]
            {
                new Claim(GatewayClaimType, route.GatewayId),
                new Claim(ClaimTypes.NameIdentifier, route.GatewayId),
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
