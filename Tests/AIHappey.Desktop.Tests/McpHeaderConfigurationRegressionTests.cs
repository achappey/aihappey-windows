using System.Text.Json;
using AIHappey.Desktop.Core;

internal static class McpHeaderConfigurationRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        void Reject(Action action, string label)
        { try { action(); throw new Exception("Accepted invalid input: " + label); } catch (InvalidOperationException) { check(true, label); } }
        var header = new McpRegistryHeader { Name = "Authorization", Value = "Bearer {api_key}", IsRequired = true,
            Variables = new() { ["api_key"] = new() { IsSecret = true, IsRequired = true, Placeholder = "Provider key" } } };
        var item = new McpCatalogItem("registry:test", "test/images", "", "https://mcp.example/images") { HeaderDefinitions = [header] };
        foreach (var name in new[] { "test.example/auth", "arbitrary.reverse.namespace/auth", "localhost/auth" })
        {
            var hint = new Dictionary<string, object> { [name] = new { type = "oauth2", resourceMetadataUrl = "https://untrusted.example/metadata" } };
            var entry = JsonSerializer.SerializeToElement(new { server = new Dictionary<string, object> { ["_meta"] = new Dictionary<string, object>
                { ["io.modelcontextprotocol.registry/publisher-provided"] = hint } } });
            check(McpHeaderConfiguration.HasOAuthHint(entry), "MCP auth presentation hint discovered generically: " + name);
            check(McpHeaderConfiguration.HasOAuthHint(JsonSerializer.SerializeToElement(new { _meta = hint })), "MCP legacy hint discovered generically: " + name);
        }
        check(!McpHeaderConfiguration.HasOAuthHint(JsonSerializer.SerializeToElement(new { _meta = (object?)null })), "MCP null legacy metadata remains supported");
        check(McpHeaderConfiguration.RequiresConfiguration(item), "MCP required API key opens configuration before install");
        check(!McpHeaderConfiguration.RequiresConfiguration(item with { OAuthManaged = true }), "MCP OAuth access token never prompts for manual input");
        check(McpHeaderConfiguration.Initial(item with { OAuthManaged = true }).Count == 0, "MCP OAuth placeholder never becomes a saved custom header");
        var fields = McpHeaderConfiguration.Fields([header], false, new Dictionary<string, string> { ["authorization"] = "Bearer old" });
        check(fields.Single().Value == "old" && fields.Single().Input.IsSecret, "MCP existing templated credential decomposed and marked secret");
        fields.Single().Value = "caller-secret";
        var resolved = McpHeaderConfiguration.Resolve([header], fields, [new("X-Tenant", "tenant")]);
        check(resolved["Authorization"] == "Bearer caller-secret" && resolved["X-Tenant"] == "tenant", "MCP variables resolve while retaining custom headers");
        check(header.Variables!["api_key"].Value is null, "MCP input drafts never mutate public registry declarations");
        var raw = JsonSerializer.Serialize(resolved);
        check(McpHeaderConfiguration.Parse(raw).SequenceEqual(resolved), "MCP form values round-trip raw JSON without losing headers");
        var custom = McpHeaderConfiguration.Fields([header], false, new Dictionary<string, string> { ["Authorization"] = "Custom original credential" });
        check(McpHeaderConfiguration.Resolve([header], custom, [])["Authorization"] == "Custom original credential", "MCP custom raw authorization remains lossless");
        foreach (var invalid in new[] { "[]", "null", "{\"X-Key\":1}", "{\"X-Key\":\"one\",\"x-key\":\"two\"}", "{\"Host\":\"evil\"}", "{\"X-Key\":\"bad\\r\\nvalue\"}" })
            Reject(() => McpHeaderConfiguration.Parse(invalid), "MCP invalid raw headers rejected: " + invalid);
        Reject(() => McpHeaderConfiguration.Validate([header], false, new Dictionary<string, string> { ["Authorization"] = "Bearer {api_key}" }), "MCP unresolved templates rejected");
        foreach (var invalid in new[] {
            new McpRegistryHeader { Name = "X-Choice", Choices = ["one"], Default = "two" },
            new McpRegistryHeader { Name = "X-Count", Format = "number", Default = "NaN" },
            new McpRegistryHeader { Name = "X-Boolean", Format = "boolean", Default = "yes" } })
            Reject(() => McpHeaderConfiguration.Resolve([invalid], McpHeaderConfiguration.Fields([invalid], false, new Dictionary<string, string>()), []), "MCP input format/choices validated: " + invalid.Name);
        var fixedItem = item with { HeaderDefinitions = [new() { Name = "X-Region", Default = "eu", Choices = ["eu", "us"] }] };
        check(!McpHeaderConfiguration.RequiresConfiguration(fixedItem) && McpHeaderConfiguration.Initial(fixedItem)["X-Region"] == "eu", "MCP valid defaults allow unchanged install path");
        var server = item.CreateServer(); server.Headers = resolved;
        check(McpHeaderConfiguration.HasManualCredentials(server), "MCP explicit Authorization takes precedence");
        var ordinary = item.CreateServer(); ordinary.Headers["X-Tenant"] = "tenant";
        check(!McpHeaderConfiguration.HasManualCredentials(ordinary), "MCP non-auth custom header does not disable OAuth");
        ordinary.Headers["X-Api-Key"] = "legacy-key";
        check(McpHeaderConfiguration.HasManualCredentials(ordinary), "MCP legacy custom API-key headers retain manual authentication");
        ordinary.Headers.Clear(); ordinary.HeaderDefinitions = [new() { Name = "Vendor-Credential", IsSecret = true }]; ordinary.Headers["Vendor-Credential"] = "key";
        check(McpHeaderConfiguration.HasManualCredentials(ordinary), "MCP registry secret declarations identify arbitrary credential header names");

        var store = new DesktopMcpStore(Path.Combine(root, "header-config"));
        await store.SaveAsync("account", [server]);
        var storedText = string.Join("", Directory.GetFiles(Path.Combine(root, "header-config")).Select(File.ReadAllText));
        check(!storedText.Contains("caller-secret") && !storedText.Contains("Bearer old"), "MCP resolved credentials remain encrypted at rest");
        var loaded = (await store.LoadAsync("account")).Servers.Single();
        check(loaded.Headers["Authorization"] == "Bearer caller-secret" && loaded.HeaderDefinitions.Single().Variables!["api_key"].IsSecret,
            "MCP public declarations and encrypted credentials survive reload");
        check(server.Clone().HeaderDefinitions.Count == 1 && server.CatalogItem.HeaderDefinitions.Count == 1, "MCP declarations retained through clone and installed-card projection");
        var legacyServer = new DesktopMcpServer { Name = "legacy", Url = "https://legacy.example/mcp", Headers = new() { ["X-Api-Key"] = "old-key" } };
        await store.SaveAsync("legacy", [legacyServer]);
        check((await store.LoadAsync("legacy")).Servers.Single().Headers["X-Api-Key"] == "old-key", "MCP existing URL-only installations still load");

        var multi = new McpRegistryHeader { Name = "X-Composite", Description = "Header description", Value = "{user}:{key}", IsRequired = true,
            Variables = new() { ["user"] = new() { IsRequired = true }, ["key"] = new() { IsRequired = true, IsSecret = true } } };
        var partial = item.CreateServer(); partial.HeaderDefinitions = [multi];
        var partialFields = McpHeaderConfiguration.Fields([multi], false, new Dictionary<string, string>());
        partialFields.Single(field => field.Variable == "user").Value = "account";
        partial.Headers = McpHeaderConfiguration.Resolve([multi], partialFields, [], validate: false);
        check(partial.Headers["X-Composite"] == "account:{key}" && !McpHeaderConfiguration.CanConnect(partial),
            "MCP partial multi-variable draft can be saved but cannot connect");
        await store.SaveAsync("partial", [partial]);
        var partialReloaded = (await store.LoadAsync("partial")).Servers.Single();
        var reopened = McpHeaderConfiguration.Fields(partialReloaded.HeaderDefinitions, false, partialReloaded.Headers);
        check(reopened.Single(field => field.Variable == "user").Value == "account" && reopened.Single(field => field.Variable == "key").Value == "",
            "MCP partial draft reopens with completed and missing variables intact");
        var factory = new CountingFactory();
        await using var manager = new DesktopMcpManager(factory, new DesktopMcpStore(Path.Combine(root, "readiness")));
        await manager.LoadAsync("fixture", default); partial.Enabled = true;
        await manager.InstallAsync(partial, default); await manager.SetEnabledAsync(partial.Id, true, default);
        await manager.ReconnectAsync(default); await manager.SignInAsync(partial.Id, default);
        check(factory.Calls == 0 && manager.Servers.Single().State == McpConnectionState.Error,
            "MCP install/enable/reconnect/sign-in never reach transport for incomplete manual credentials");
        reopened.Single(field => field.Variable == "key").Value = "finished";
        partial.Headers = McpHeaderConfiguration.Resolve([multi], reopened, []);
        await manager.InstallAsync(partial, default);
        check(factory.Calls == 1 && McpHeaderConfiguration.CanConnect(partial), "MCP completing saved variables enables the transport path");
        var oauthServer = item.CreateServer(); oauthServer.OAuthManaged = true;
        check(McpHeaderConfiguration.CanConnect(oauthServer), "MCP OAuth-managed token remains connectable without manual credential input");
    }

    private sealed class CountingFactory : IDesktopMcpClientFactory
    {
        public int Calls;
        public Task<IDesktopMcpConnection> ConnectAsync(DesktopMcpServer server, CancellationToken ct)
        { Calls++; return Task.FromException<IDesktopMcpConnection>(new HttpRequestException("Fixture stops before network")); }
    }
}
