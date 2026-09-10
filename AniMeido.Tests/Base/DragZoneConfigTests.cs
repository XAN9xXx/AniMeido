using AniMeido.Plugin.Base.Models;

namespace AniMeido.Tests
{
    public class DragZoneConfigTests
    {
        [Fact]
        public void GetDefaults_ReturnsFourZones()
        {
            var defaults = DragZoneConfig.GetDefaults();
            Assert.Equal(4, defaults.Count);
        }

        [Fact]
        public void GetDefaults_AllActionsAreDistinct()
        {
            var defaults = DragZoneConfig.GetDefaults();
            var actions = defaults.Select(z => z.Action).Distinct().ToList();
            // 4 zones, each with a distinct action (None/Watching/PlanToWatch/NotInterested)
            Assert.Equal(4, actions.Count);
        }

        [Fact]
        public void DragAction_Enum_HasAllEightValues()
        {
            var values = Enum.GetValues<DragAction>();
            Assert.Equal(8, values.Length);
        }

        [Fact]
        public void SerializeAndDeserialize_PreservesData()
        {
            var configs = DragZoneConfig.GetDefaults();
            var json = System.Text.Json.JsonSerializer.Serialize(configs);
            var deserialized = System.Text.Json.JsonSerializer.Deserialize<List<DragZoneConfig>>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(configs.Count, deserialized!.Count);
            Assert.Equal(configs[0].Action, deserialized[0].Action);
        }
    }
}
