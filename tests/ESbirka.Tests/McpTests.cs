using System.Reflection;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ESbirka.Client;
using ESbirka.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ESbirka.Tests;

public sealed class McpTests
{
    [Fact]
    public async Task HttpServerNegotiatesAndListsTools()
    {
        var directory = Path.Combine(Path.GetTempPath(), "esbirka-mcp-http-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{typeof(LawTools).Assembly.Location}\" http",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment =
            {
                ["ESbirka__CachePath"] = Path.Combine(directory, "cache.db"),
                ["Mcp__HttpUrl"] = $"http://127.0.0.1:{port}"
            }
        }) ?? throw new InvalidOperationException("Failed to start the HTTP MCP server.");

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var readinessClient = new HttpClient();
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    using var response = await readinessClient.GetAsync(endpoint, timeout.Token);
                    break;
                }
                catch (HttpRequestException) when (!process.HasExited)
                {
                    await Task.Delay(50, timeout.Token);
                }
            }

            var transport = new HttpClientTransport(new()
            {
                Name = "esbirka-http-test",
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp
            });
            await using (var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token))
            {
                var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
                Assert.Equal(["get_law_provision", "list_law_structure", "list_law_versions", "search_laws"], tools.Select(tool => tool.Name).Order());
            }
            process.Kill(true);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Empty(await process.StandardOutput.ReadToEndAsync(timeout.Token));
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task StdioServerNegotiatesSchemasAndReturnsToolErrorsWithoutStdoutNoise()
    {
        var directory = Path.Combine(Path.GetTempPath(), "esbirka-mcp-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var transport = new StdioClientTransport(new()
            {
                Name = "esbirka-test",
                Command = "dotnet",
                Arguments = [typeof(LawTools).Assembly.Location],
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["ESbirka__CachePath"] = Path.Combine(directory, "cache.db")
                }
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            Assert.Equal(4, tools.Count);
            var provision = Assert.Single(tools, tool => tool.Name == "get_law_provision");
            Assert.NotNull(provision.ProtocolTool.OutputSchema);
            Assert.True(provision.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("publicationNumber", out _));
            Assert.False(provision.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("cancellationToken", out _));
            var result = await client.CallToolAsync("get_law_provision", new Dictionary<string, object?>
            {
                ["publicationNumber"] = "65/2022", ["section"] = "1", ["refreshPolicy"] = "bypass-cache"
            }, cancellationToken: timeout.Token);
            Assert.True(result.IsError);
            Assert.Contains("invalid_argument", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
            var search = Assert.Single(tools, tool => tool.Name == "search_laws");
            Assert.NotNull(search.ProtocolTool.OutputSchema);
            var properties = search.ProtocolTool.InputSchema.GetProperty("properties");
            Assert.True(properties.TryGetProperty("query", out _));
            Assert.True(properties.TryGetProperty("page", out _));
            Assert.True(properties.TryGetProperty("pageSize", out _));
            Assert.False(properties.TryGetProperty("cancellationToken", out _));
            var searchError = await client.CallToolAsync("search_laws", new Dictionary<string, object?>
            {
                ["query"] = "law", ["pageSize"] = 101
            }, cancellationToken: timeout.Token);
            Assert.True(searchError.IsError);
            Assert.Contains("invalid_argument", Assert.Single(searchError.Content.OfType<TextContentBlock>()).Text);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task WholeHistoryPurgeRequiresConfirmationUnlessDryRun()
    {
        var administration = new FakeAdministration();
        Assert.NotEqual(0, await CacheCommands.RunAsync(["cache", "purge-document", "65/2022", "--include-history"], administration));
        Assert.Equal(0, administration.Calls);
        Assert.Equal(0, await CacheCommands.RunAsync(["cache", "purge-document", "65/2022", "--include-history", "--dry-run"], administration));
        Assert.True(administration.DryRun);
        Assert.True(administration.IncludeHistory);
        Assert.Equal(0, await CacheCommands.RunAsync(["cache", "purge-document", "65/2022", "--include-history", "--confirm"], administration));
        Assert.False(administration.DryRun);
        Assert.Equal(2, administration.Calls);
    }

    [Fact]
    public async Task ToolMapsArgumentsAndPreservesRequestedAndResolvedVersions()
    {
        var client = new FakeClient();
        var tools = new LawTools(client);
        var result = await tools.GetLawProvision("65/2022", "2030-01-01", section: "1", letter: "a", refreshPolicy: "revalidate", includeReferences: false);
        Assert.Equal(new DateOnly(2030, 1, 1), client.Version!.AsOf);
        Assert.Equal("a", client.Selector!.Letter);
        Assert.Equal(RefreshPolicy.Revalidate, client.Options!.RefreshPolicy);
        Assert.False(client.Options.IncludeReferences);
        Assert.Equal(client.Version, result.RequestedVersion);
        Assert.Equal(new DateOnly(2025, 9, 3), result.ResolvedVersion.EffectiveFrom);
    }

    [Fact]
    public async Task InvalidModeAndNotFoundAreActionableMcpErrors()
    {
        var tools = new LawTools(new FakeClient { NotFound = true });
        Assert.StartsWith("invalid_argument:", (await Assert.ThrowsAsync<McpException>(() => tools.GetLawProvision("65/2022", "2025-01-01", "promulgated", section: "1"))).Message);
        Assert.StartsWith("provision_not_found:", (await Assert.ThrowsAsync<McpException>(() => tools.GetLawProvision("65/2022", section: "1"))).Message);
        Assert.StartsWith("invalid_argument:", (await Assert.ThrowsAsync<McpException>(() => tools.GetLawProvision("65/2022", section: "1", refreshPolicy: "bypass-cache"))).Message);
    }

    [Fact]
    public async Task SearchToolMapsArgumentsErrorsAndResponseLimit()
    {
        var client = new FakeClient();
        var tools = new LawTools(client);
        using var cancellation = new CancellationTokenSource();
        var result = await tools.SearchLaws("89/2012", 2, 5, cancellation.Token);
        Assert.Equal("89/2012", client.SearchQuery);
        Assert.Equal(2, client.SearchPage);
        Assert.Equal(5, client.SearchPageSize);
        Assert.Equal(cancellation.Token, client.SearchToken);
        Assert.Equal("sm", Assert.Single(result.Items).Collection);
        Assert.Null(result.Items[0].PublicationNumber);
        Assert.Equal("89/2012 Sb. m. s.", result.Items[0].PublicationCode);
        await tools.SearchLaws("TZ");
        Assert.Equal(0, client.SearchPage);
        Assert.Equal(20, client.SearchPageSize);
        client.SearchTitle = new string('x', 512 * 1024);
        Assert.StartsWith("result_too_large:", (await Assert.ThrowsAsync<McpException>(() => tools.SearchLaws("law"))).Message);
        client.SearchError = new("upstream_contract_changed", "Fixture contract failure");
        Assert.StartsWith("upstream_contract_changed:", (await Assert.ThrowsAsync<McpException>(() => tools.SearchLaws("law"))).Message);
    }

    [Fact]
    public void OnlyReadOnlyLegalOperationsAreMcpTools()
    {
        var tools = typeof(LawTools).GetMethods().Select(method => method.GetCustomAttribute<McpServerToolAttribute>()).OfType<McpServerToolAttribute>().ToArray();
        Assert.Equal(["get_law_provision", "list_law_structure", "list_law_versions", "search_laws"], tools.Select(tool => tool.Name).Order());
        Assert.All(tools, tool => { Assert.True(tool.ReadOnly); Assert.False(tool.Destructive); Assert.True(tool.UseStructuredContent); });
    }

    private sealed class FakeAdministration : IESbirkaCacheAdministration
    {
        public int Calls;
        public bool DryRun;
        public bool IncludeHistory;
        public Task<CachePurgeResult> PurgeDocumentAsync(string publicationNumber, bool includeHistoricalVersions, bool dryRun = false, CancellationToken cancellationToken = default)
        {
            Calls++;
            DryRun = dryRun;
            IncludeHistory = includeHistoricalVersions;
            return Task.FromResult(new CachePurgeResult(0, 0, dryRun));
        }
        public Task RevalidateCurrentAsync(string publicationNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PurgeCurrentAliasAsync(string publicationNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PurgeProvisionAsync(string publicationNumber, LawVersionSelector version, LegalProvisionSelector provision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PurgeVersionAsync(string publicationNumber, DateOnly effectiveFrom, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeClient : IESbirkaClient
    {
        public LawVersionSelector? Version;
        public LegalProvisionSelector? Selector;
        public ProvisionOptions? Options;
        public bool NotFound;
        public string? SearchQuery;
        public int SearchPage;
        public int SearchPageSize;
        public CancellationToken SearchToken;
        public string SearchTitle = "Treaty";
        public ESbirkaException? SearchError;

        public Task<LawSearchResult> SearchAsync(string query, int page = 0, int pageSize = 20, CancellationToken cancellationToken = default)
        {
            SearchQuery = query;
            SearchPage = page;
            SearchPageSize = pageSize;
            SearchToken = cancellationToken;
            if (SearchError is not null) throw SearchError;
            return Task.FromResult(new LawSearchResult(query, page, pageSize, 1, false,
                [new("sm", "89/2012 Sb. m. s.", null, SearchTitle, "/sm/2012/89/0000-00-00", "VYHLASENY_BEZ_UCINNOSTI", new(2012, 10, 29))], DateTimeOffset.UtcNow));
        }

        public Task<LawProvision?> GetProvisionAsync(string publicationNumber, LawVersionSelector version,
            LegalProvisionSelector provision, ProvisionOptions? options = null, CancellationToken cancellationToken = default)
        {
            Version = version;
            Selector = provision;
            Options = options;
            var freshness = new CacheFreshness("hit", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            return Task.FromResult<LawProvision?>(NotFound ? null : new(publicationNumber, version,
                new("/sb/2022/65/2025-09-03", "/eli/cz/sb/2022/65/2025-09-03", new(2025, 9, 3), null, "AKTUALNI", "Law", []),
                provision, "citation", "eli", "url", true, "text", null, [], [], freshness));
        }
        public Task<IReadOnlyList<LawStructureNode>> GetStructureAsync(string publicationNumber, LawVersionSelector version, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LawStructureNode>>([]);
        public Task<IReadOnlyList<LawVersion>> GetVersionsAsync(string publicationNumber, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LawVersion>>([]);
    }
}