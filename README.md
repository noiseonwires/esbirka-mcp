# e-Sbirka Client And MCP

Version-aware retrieval of Czech legal provisions from e-Sbirka, as a reusable .NET 10 library and a standalone MCP server supporting stdio and Streamable HTTP.

The server uses anonymous web-application endpoints, not the registered public API. Published API schemas overlap, but website behavior can differ from their descriptions. Provision results include the requested date, actual server-resolved version, effective interval, and cache freshness. A future date resolving to the current text does not establish future legal validity.

## Quick Start

Install the .NET 10 SDK, open this directory in VS Code, and build:

```sh
dotnet build ESbirka.slnx
dotnet test ESbirka.slnx
```

Start `esbirka-law-provisions` from the CodeLens in `.vscode/mcp.json`, or run **MCP: List Servers**. Build before starting: `--no-build` keeps build messages off the protocol stream. The server uses stdout only for MCP and writes logs to stderr.

You can debug in VS Code: start the MCP server, choose **Attach to e-Sbirka MCP**, and select its `ESbirka.Mcp` process. C# debugging support is required.

For other MCP clients, publish and configure a stdio command:

```sh
dotnet publish src/ESbirka.Mcp/ESbirka.Mcp.csproj -c Release -o artifacts/mcp
```

```json
{
  "servers": {
    "esbirka": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["/absolute/path/to/artifacts/mcp/ESbirka.Mcp.dll"]
    }
  }
}
```

Do not launch an interactive terminal and expect a prompt: the no-argument process waits for MCP messages on stdin.

For network clients, start the Streamable HTTP server:

```sh
dotnet artifacts/mcp/ESbirka.Mcp.dll http
```

It listens at `http://127.0.0.1:3001/mcp` by default. Configure an HTTP MCP client with that endpoint. Set `Mcp__HttpUrl` to change the listener (for example, `http://0.0.0.0:3001`) and `Mcp__HttpPath` to change `/mcp`. The endpoint has no application authentication, so keep the default loopback binding unless access is restricted by a trusted network boundary or reverse proxy.

The Docker image runs Streamable HTTP mode on port 3001 and persists its cache under `/data`. Docker selects the native `linux/amd64` or `linux/arm64` base image automatically:

```sh
docker compose up --build
```

The included Compose configuration publishes the endpoint only on `127.0.0.1:3001`. For a remote deployment, terminate TLS and authentication at a trusted reverse proxy rather than publishing the unauthenticated container port directly.

## Tools

| Tool | Purpose |
| --- | --- |
| `get_law_provision` | Select a part, chapter, division, section, paragraph, letter, or point, including descendants by default. |
| `list_law_structure` | Discover semantic selectors and headings; `maxDepth` defaults to 3 addressable levels. |
| `list_law_versions` | List effective intervals, canonical paths, producing amendments, and the distinct promulgated version. |
| `search_laws` | Search legal acts across collections by text, title, alias, or publication code, ranked by relevance. |

Example `search_laws` arguments:

```json
{"query":"89/2012","page":0,"pageSize":20}
```

Search returns one page with `totalCount`, `hasMore`, `retrievedAt`, and `items`. `page` is a zero-based page index, not an item offset; increment it by one while keeping the query and page size unchanged. `pageSize` defaults to 20 and must be 1-100; the nonblank query is limited to 2000 characters. Search uses `POST /sbr-cache/jednoducha-vyhledavani` with `start = page`, `pocet = pageSize`, and `razeni = ["+relevance"]`, matching the website rather than the differing pagination/sort descriptions in its published OpenAPI definition.

Each item preserves `collection`, `publicationCode`, `title`, relative `stableUrl`, upstream `status`, and optional `publishedOn` (publication date, not effective date). Only `/sb/` results have a non-null `publicationNumber` accepted by the existing retrieval tools. For example, a result for `89/2012 Sb. m. s.` belongs to `/sm/` and must not be passed as `89/2012` to those tools. A publication-code query is not an exact lookup. Search neither resolves requested effective dates nor returns provision text; use the retrieval tools for that. Search results are not cached and may change between pages. Requests share the normal rate, timeout, response-size, and retry limits. Reduce `pageSize` on `result_too_large`.

Example `get_law_provision` arguments:

```json
{
  "publicationNumber": "65/2022",
  "asOf": "2023-05-15",
  "section": "1",
  "paragraph": "1",
  "letter": "a",
  "includeReferences": true,
  "refreshPolicy": "prefer-cache"
}
```

Omit `asOf` for current text. For as-published text, use `versionMode: "promulgated"` without `asOf`; the internal `0000-00-00` sentinel is not a public date. Selectors accept optional Czech labels and numeric suffixes such as `7aa`. Supply enclosing part/chapter components when a partial selector is ambiguous.

Refresh policies are `prefer-cache`, `revalidate`, and `cache-only`. Revalidation checks metadata and reuses unchanged canonical pages. There is no unrestricted bypass. Stale data requires `allowStale: true` and is still bounded by `MaximumStaleAge`. Ineffective roots require `includeIneffective: true`. Plain text excludes script/style content; optional XHTML is raw, untrusted upstream markup and must never be rendered unsanitized.

MCP results are capped at 512 KiB. Oversized results fail explicitly rather than silently truncating legal text; request a narrower provision or shallower outline. Errors retain codes such as `invalid_argument`, `ambiguous_selector`, `provision_not_found`, `version_not_available`, `upstream_unavailable`, and `upstream_contract_changed`. Use `list_law_structure` to recover from a missing selector.

## Library

`src/ESbirka.Client` has no MCP, Selenium, or Azure OpenAI dependency. Its public entry points are `IESbirkaClient`, `IESbirkaContentFetcher`, `IESbirkaCache`, and `IESbirkaCacheAdministration`. Consumers that need browser, authenticated, or other retrieval behavior can implement `IESbirkaContentFetcher` and supply it when constructing or registering the client. Pack it with:

```sh
dotnet pack src/ESbirka.Client/ESbirka.Client.csproj -c Release -o artifacts
```

Direct construction does not require dependency injection:

```csharp
using ESbirka.Client;

using var http = new HttpClient();
var options = new ESbirkaClientOptions { CachePath = "data/esbirka.db" };
using var coordinator = new ESbirkaRequestCoordinator();
using var client = new ESbirkaClient(
    new HttpContentFetcher(http), new SqliteESbirkaCache(options), options, coordinator);
var provision = await client.GetProvisionAsync(
    "65/2022", new LawVersionSelector(),
    new LegalProvisionSelector(Section: "1", Paragraph: "1", Letter: "a"));
var matches = await client.SearchAsync("89/2012", page: 0, pageSize: 20);
```

Custom `IESbirkaContentFetcher` implementations must implement `PostJsonAsync` to support search. Existing GET-only implementations remain compatible with retrieval and report `search_not_supported` for search requests.

For DI, register a host-supplied `IESbirkaContentFetcher`, then call `AddESbirkaClient(options => ...)`. The client is scoped, while the cache and request coordinator are shared singletons. The standalone MCP host promotes its client to singleton to reuse its bounded in-memory indexes across calls. Share one coordinator per upstream host when constructing multiple clients manually.

## GitHub Packages

`dotnet pack` only creates a local `.nupkg`; it does not publish to a registry. The **Build And Test** workflow uploads the Ubuntu test job's package as an Actions artifact named `ESbirka.Client`. Download it from the workflow run's **Artifacts** section after that job succeeds.

Publishing to the private NuGet registry is a separate job, gated on the Windows and Linux tests. Normal pushes and pull requests do not publish. After these workflow changes reach the default branch, choose either:

1. Open **Actions > Build And Test > Run workflow**, select the default branch, enable **Publish ESbirka.Client to GitHub Packages after verification**, and run it.
2. Publish a GitHub release from a commit containing this workflow. Use a tag matching the package version, such as `v0.1.1`.

The package version comes from `<Version>` in `src/ESbirka.Client/ESbirka.Client.csproj`, currently `0.1.1`; release tags do not automatically change it. Increment this property before publishing changed code as a new version. Re-running a publication skips existing versions rather than replacing them. A failed verification job prevents publication; inspect the run's jobs if no package appears.

The publishing job uses the ephemeral `GITHUB_TOKEN` with `packages: write`; no PAT or new repository secret is required. NuGet source credentials are supplied through a job environment variable, not committed to configuration. The package's `RepositoryUrl` links it to `noiseonwires/esbirka-mcp`. New GitHub Packages packages are private by default.

After the first successful publication, `ESbirka.Client` will appear in the repository's **Packages** section and can be restored from:

```text
https://nuget.pkg.github.com/noiseonwires/index.json
```

To let another repository's GitHub Actions restore it, open the package's **Package settings > Manage Actions access**, add that repository with **Read** access, and give its workflow `packages: read`. Authenticate its NuGet source using its own `GITHUB_TOKEN`. Local authenticated restores generally require a classic PAT with `read:packages` from an account permitted to access the private package. Never commit tokens; use CI environment variables for credentials.

## Storage And Fetching

- Resolve metadata first, then fetch every page using the canonical dated path. `%2F` remains encoded inside one path segment.
- Preserve ordered fragments, unknown types/fields, references and raw XHTML. Hierarchy uses one depth stack across all pages; legal addresses use semantic ELI suffixes, never durable numeric IDs.
- Persist compressed, SHA-256-checked raw metadata and pages as a complete SQLite snapshot. Snapshot and alias writes share one transaction. Incomplete or inconsistent downloads are never published.
- Keep parsed indexes in a size-bounded in-memory cache, keyed by parser version and payload hash. Rebuild from raw pages after a parser change without fetching upstream. Rendering is intentionally not persisted.
- Coalesce misses per document, serialize upstream activity through a shared coordinator, and apply request delay, cancellation, size/page limits, and bounded retries with backoff/jitter.
- Keep old canonical snapshots after alias movement. Quota eviction removes least-recently-used historical snapshots first, preserving current pointers and snapshots in the active write transaction. If active snapshots exhaust the quota, writes fail atomically with `cache_full`.
- Cache only confirmed provision misses, briefly and in memory; never cache transport errors. No negative law cache is used.
- The quota measures compressed payload bytes, not SQLite file allocation. SQLite reuses freed pages; physical file compaction is an offline maintenance concern.

The standalone host uses `HttpContentFetcher` and supports an optional proxy. Set `Transport__UserAgent` to override the User-Agent for all upstream GET and search POST requests, for example `MyLegalClient/1.0 (+https://example.com/contact)`. The value must be a nonblank, valid HTTP User-Agent header. Library consumers can use `new HttpContentFetcher(http) { UserAgent = "MyLegalClient/1.0" }`. It does not disable certificate verification or web security.

Use a single MCP process per cache database. Multiple independent caches/processes do not coordinate upstream rates. Distributed caching, job-pinning APIs, scheduled effective-date sweeps, background stale-while-revalidate, and comparisons/diffs are future extensions. Refresh is synchronous and explicit in this implementation.

## Configuration

Standard .NET configuration applies: environment variables override optional `appsettings.json` in the process working directory. Use double underscores for nesting. No credentials are required by e-Sbirka.

| Environment Variable | Default |
| --- | --- |
| `ESbirka__BaseUri` | `https://e-sbirka.gov.cz/` |
| `ESbirka__CachePath` | OS local application data directory, `esbirka/cache.db` |
| `ESbirka__RequestDelay` | `00:00:01` |
| `ESbirka__RequestTimeout` | `00:01:30` |
| `ESbirka__CurrentAliasTtl` | `02:00:00` |
| `ESbirka__VersionListTtl` | `12:00:00` |
| `ESbirka__NegativeCacheTtl` | `00:05:00` |
| `ESbirka__MaximumStaleAge` | `1.00:00:00` |
| `ESbirka__MaximumResponseBytes` | `16777216` |
| `ESbirka__MaximumSnapshotBytes` | `134217728` |
| `ESbirka__MaximumPages` | `100` |
| `ESbirka__RetryCount` | `2` |
| `ESbirka__MaximumCacheBytes` | `536870912` |
| `ESbirka__MemoryCacheBytes` | `33554432` |
| `Transport__Proxy` | Unset; HTTP/SOCKS proxy URI without embedded credentials |
| `Transport__UserAgent` | `Mozilla/5.0 ESbirka.Client/0.1` |
| `Mcp__HttpUrl` | `http://127.0.0.1:3001` |
| `Mcp__HttpPath` | `/mcp` |

The `ESbirka.Client` meter exposes remote request/byte counts, cache reads, stale refresh failures, and contract failures. Structured logs identify alias movements and purge record/byte counts without logging law bodies.

## Local Administration

Use the published host or `dotnet run --project src/ESbirka.Mcp --` before these arguments:

```text
cache revalidate-current 65/2022
cache purge-current-alias 65/2022
cache purge-provision 65/2022 --as-of 2025-09-03 --section 1 --paragraph 1
cache purge-version 65/2022 --effective-from 2025-09-03
cache purge-document 65/2022 --dry-run
cache purge-document 65/2022
cache purge-document 65/2022 --include-history --dry-run
cache purge-document 65/2022 --include-history --confirm
```

Administration is local, authorized by OS access to the cache file, and never registered as MCP tools. Whole-history deletion requires confirmation. Provision purge clears in-process derived/negative entries while retaining raw pages; a separate CLI process has no persisted rendering to delete. For an upstream correction under the same canonical date, purge the version and fetch it again. No upstream single-fragment endpoint is assumed. Current-document purge preserves older snapshots unless `--include-history` is specified.

## Verification

The ordinary xUnit/VSTest suite uses deterministic fixtures and a fake content fetcher, plus a real network-free stdio MCP session. External tests are skipped by default:

```powershell
$env:ESBIRKA_LIVE_TESTS = '1'
dotnet test ESbirka.slnx --filter Category=Live
Remove-Item Env:ESBIRKA_LIVE_TESTS
```

The live tests check 65/2022 current/promulgated/historical/future behavior, the paginated Civil Code's section 310 and chapter hierarchy, and search page-index semantics. They use one-second upstream delays and bounded timeouts. GitHub Actions runs the offline tests on Windows and Linux.

## Related Projects

[scimorph/eur-lex-mcp](https://github.com/scimorph/eur-lex-mcp) is a similar MCP project implemented in Python, enabling AI agents and applications to query up-to-date EU regulations through EUR-Lex.