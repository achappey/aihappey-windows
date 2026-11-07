using System.Reflection;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckEndpointMenusAsync(ElementTheme theme)
    {
        foreach (var direct in new[] { false, true })
        {
            var host = new EndpointUiHost(direct);
            var session = new DesktopSession(host, new AgentUiRuntime(), new());
            var shell = new ChatShell(session) { RequestedTheme = theme };
            var root = new Grid(); window!.Content = root; root.Children.Add(shell);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            void Set(string name, object value) => typeof(ChatShell).GetField(name, flags)!.SetValue(shell, value);
            void Build() => Call(shell, "BuildProviderSettingsMenu");
            var context = "Endpoint menus / " + theme + " / " + (direct ? "Header" : "Entra");
            try
            {
                Set("initialized", true);
                var models = new[] { new ChatTarget("openai/chat", "Chat") { ProviderKey = "openai", ModelType = "language" },
                    new ChatTarget("anthropic/chat", "Claude") { ProviderKey = "anthropic", ModelType = "language" } };
                Set("targets", models); Set("aiModelTargets", models);
                typeof(ChatShell).GetProperty("TargetId", flags)!.SetValue(shell, models[0].Id);
                await Task.Delay(80); shell.UpdateLayout();
                Build();
                var menu = Field<MenuFlyoutSubItem>(shell, "providerSettings");
                MenuFlyoutSubItem Gateway() => menu.Items.OfType<MenuFlyoutSubItem>().Single(item => item.Name == "GatewayChatEndpoints");
                MenuFlyoutSubItem Direct() => menu.Items.OfType<MenuFlyoutSubItem>().Single(item => item.Name == "DirectChatEndpoints");
                checkOrder();
                void checkOrder()
                {
                    var gateway = Gateway();
                    Check(gateway.Items.OfType<ToggleMenuFlyoutItem>().Select(item => item.Text).SequenceEqual(DesktopChatRoutes.GatewayEndpoints), context + ": Gateway exposes four native protocol choices in browser order");
                    Check(menu.Items.IndexOf(gateway) < menu.Items.ToList().FindIndex(item => item is MenuFlyoutItem { Name: "ProviderType_language" }), context + ": endpoints precede capability groups");
                    Check(gateway.RequestedTheme == theme && gateway.Items.OfType<ToggleMenuFlyoutItem>().All(item => item.RequestedTheme == theme), context + ": native submenu items follow the current theme");
                }
                Check(Gateway().Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.IsChecked).Text == "/api/chat", context + ": existing installs default to the current chat protocol");
                Check(menu.Items.OfType<MenuFlyoutSubItem>().Any(item => item.Name == "DirectChatEndpoints") == direct, context + ": Direct visibility is owned by host capability");
                var conversation = Field<Conversation>(shell, "current"); var partition = session.HistoryPartition;
                var input = Field<TextBox>(shell, "input"); input.Text = "Keep my draft";
                var select = typeof(ChatShell).GetMethod("SelectChatEndpointAsync", flags)!;
                await (Task)select.Invoke(shell, ["gateway", "/v1/responses", "openai"])!;
                Build();
                Check(session.Settings.Chat.Endpoint == "/v1/responses" && Gateway().Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.IsChecked).Text == "/v1/responses", context + ": selection updates checked state");
                var saved = await SettingsStore.LoadAsync(session.DataDirectory, new());
                Check(saved.Chat.Endpoint == "/v1/responses" && ReferenceEquals(Field<Conversation>(shell, "current"), conversation)
                    && input.Text == "Keep my draft" && session.HistoryPartition == partition, context + ": endpoint persistence preserves the current conversation, draft and history");
                if (direct)
                {
                    Check(!Direct().IsEnabled, context + ": a provider without a configured key cannot use Direct");
                    host.HasKey = true; Build();
                    Check(Direct().IsEnabled && Direct().Items.Count > 0, context + ": key availability enables compatible Direct options on menu reopen");
                    await (Task)select.Invoke(shell, ["direct", "/v1/responses", "openai"])!; Build();
                    Check(Direct().Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.IsChecked).Text == "/v1/responses"
                        && Gateway().Items.OfType<ToggleMenuFlyoutItem>().All(item => !item.IsChecked), context + ": the selected mode has exactly one checked protocol");
                    typeof(ChatShell).GetProperty("TargetId", flags)!.SetValue(shell, "anthropic/chat"); Build();
                    Check(Direct().Items.OfType<ToggleMenuFlyoutItem>().Select(item => item.Text).SequenceEqual(["/v1/messages"])
                        && Direct().Items.OfType<ToggleMenuFlyoutItem>().Single().IsChecked, context + ": model changes resolve compatible provider protocols");
                    host.HasKey = false; Build();
                    Check(!Direct().IsEnabled && Direct().Items.OfType<ToggleMenuFlyoutItem>().All(item => !item.IsChecked), context + ": removed credentials cannot leave a stale usable Direct selection");
                }
                Set("busy", true); Build();
                Check(!Gateway().IsEnabled && (!direct || !Direct().IsEnabled), context + ": route changes are disabled during active work");
                Set("busy", false); Set("service", ServiceKind.Agents); Build();
                Check(!Gateway().IsEnabled && (!direct || !Direct().IsEnabled), context + ": AI endpoint menus cannot reroute Agents");
            }
            finally
            {
                await shell.ShutdownAsync(); root.Children.Clear();
                if (Directory.Exists(session.DataDirectory)) Directory.Delete(session.DataDirectory, true);
            }
        }
    }

    private sealed class EndpointUiHost(bool direct) : IDesktopHost
    {
        public bool HasKey;
        public string ProfileId { get; } = "endpoint-ui-" + Guid.NewGuid().ToString("N");
        public bool AllowLocal => true;
        public string AccountLabel => "Endpoint fixture";
        public string HistoryIdentity => "endpoint-fixture";
        public bool SupportsDirectChat => direct;
        public bool HasDirectChatCredential(string provider) => HasKey;
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken ct) => throw new InvalidOperationException("Endpoint UI tests must not contact a service.");
        public Task ManageAccountAsync(object root, CancellationToken ct) => Task.CompletedTask;
    }
}
