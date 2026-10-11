# MCPGateway CLI

Parent: `../../AGENTS.md`.

Purpose: NuGet .NET tool `mcpgateway` for official-SDK access to an explicitly configured MCP endpoint. Entry point: `Program.cs`. Keep server policy and publication enforcement in the gateway. Credentials come from `MCPGATEWAY_TOKEN`; never accept credentials in command arguments or log them. Endpoint comes from `--endpoint` or `MCPGATEWAY_ENDPOINT`.

Commands: `dotnet pack src/ManagedCode.MCPGateway.Cli/ManagedCode.MCPGateway.Cli.csproj -c Release -o artifacts/cli`.

Release: `.github/workflows/publish-cli.yml`; CLI version/metadata are centralized in `Directory.Build.props`. Initial name-reservation publication is explicitly authorized without local test/benchmark checks. Subsequent behavior changes follow root TUnit policy. Applicable skills: mcaf-dotnet, mcaf-dotnet-tunit.
