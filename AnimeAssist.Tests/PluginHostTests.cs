using AniMeido.App;

namespace AniMeido.Tests
{
    public class PluginHostTests
    {
        [Fact]
        public void PluginIdDeduplication_SameId_SecondIsSkipped()
        {
            var set = new PluginIdTracker();
            Assert.True(set.TryAdd("plugin.a"));
            Assert.False(set.TryAdd("plugin.a"));
        }

        [Fact]
        public void PluginIdDeduplication_DifferentId_BothAdded()
        {
            var set = new PluginIdTracker();
            Assert.True(set.TryAdd("plugin.a"));
            Assert.True(set.TryAdd("plugin.b"));
        }

        [Fact]
        public void PluginIdDeduplication_CaseInsensitive()
        {
            var set = new PluginIdTracker();
            Assert.True(set.TryAdd("Plugin.A"));
            Assert.False(set.TryAdd("plugin.a"));
        }
    }
}
