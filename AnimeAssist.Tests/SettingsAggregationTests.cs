using AniMeido.App.Services;
using AniMeido.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.Tests
{
    public class SettingsAggregationTests
    {
        private sealed class MockPlugin : IPlugin
        {
            private readonly List<PluginNavigationItem> _items;

            public MockPlugin(string id, string displayName, List<PluginNavigationItem> items)
            {
                PluginID = id;
                DisplayName = displayName;
                _items = items;
            }

            public string PluginID { get; }
            public string DisplayName { get; }
            public string Version => "1.0.0";
            public bool IsRequired => false;
            public Task InitializeAsync(IServiceCollection services) => Task.CompletedTask;
            public IEnumerable<PluginNavigationItem> GetNavigationItems() => _items;
        }

        private static PluginNavigationItem NavItem(string label, bool isSettings, Type? pageType) =>
            new(label, "\uE713", pageType?.FullName ?? "")
            {
                PageType = pageType,
                IsSettingsPage = isSettings
            };

        [Fact]
        public void CollectSettings_FiltersNonSettingsItems()
        {
            var plugin = new MockPlugin("test", "测试插件", new()
            {
                NavItem("主页", false, typeof(string)),
                NavItem("设置", true, typeof(int)),
            });

            var settings = SettingsEntryCollector.Collect(new[] { plugin });
            Assert.Single(settings);
            Assert.Equal("设置", settings[0].Label);
            Assert.Equal("\uE713", settings[0].Icon);
        }

        [Fact]
        public void CollectSettings_MultipleSettingsPages_AllCollected()
        {
            var plugin = new MockPlugin("test", "测试插件", new()
            {
                NavItem("拖放标记", true, typeof(string)),
                NavItem("数据管理", true, typeof(int)),
                NavItem("主页", false, typeof(double)),
            });

            var settings = SettingsEntryCollector.Collect(new[] { plugin });
            Assert.Equal(2, settings.Count);
        }

        [Fact]
        public void CollectSettings_PageTypeNull_Skipped()
        {
            var plugin = new MockPlugin("test", "测试插件", new()
            {
                NavItem("设置", true, null),
                NavItem("其他设置", true, typeof(int)),
            });

            var settings = SettingsEntryCollector.Collect(new[] { plugin });
            Assert.Single(settings);
        }

        [Fact]
        public void CollectSettings_Empty_ReturnsEmpty()
        {
            var settings = SettingsEntryCollector.Collect(Array.Empty<IPlugin>());
            Assert.Empty(settings);
        }

        [Fact]
        public async Task HostedSettings_UsesRegisteredInvoker()
        {
            var registry = new PluginContributionRegistry();
            string? invokedPluginId = null;
            string? invokedSettingsId = null;
            registry.SettingsInvoker = (pluginId, settingsId) =>
            {
                invokedPluginId = pluginId;
                invokedSettingsId = settingsId;
                return Task.CompletedTask;
            };

            await registry.OpenSettingsAsync(
                "AniMeido.Plugin.Test",
                "AniMeido.Plugin.Test.settings");

            Assert.Equal("AniMeido.Plugin.Test", invokedPluginId);
            Assert.Equal(
                "AniMeido.Plugin.Test.settings",
                invokedSettingsId);
        }
    }
}
