This repository contains a .NET 10 reusable e-Sbirka client and a thin stdio/HTTP MCP host.

- Keep legal endpoint construction, version semantics, pagination, parsing, caching, and purge rules in ESbirka.Client.
- ESbirka.Client must not depend on MCP, Selenium, or Azure OpenAI.
- ESbirka.Mcp owns HTTP transport composition, MCP mapping, and local administration commands.
- Never send logs to stdout in server mode; stdout belongs exclusively to MCP.
- Use the official MCP C# SDK: https://github.com/modelcontextprotocol/csharp-sdk and https://csharp.sdk.modelcontextprotocol.io/.
- Protocol reference: https://modelcontextprotocol.io/specification/.
- Use semantic ELI paths for legal selectors; numeric fragment IDs are snapshot-local.
- Publish only complete, validated canonical snapshots atomically with their aliases.
- Keep ordinary tests offline. Live tests are explicitly opt-in; see README.md.
- Run dotnet test ESbirka.slnx after relevant changes. Keep changes scoped and preserve public contracts.