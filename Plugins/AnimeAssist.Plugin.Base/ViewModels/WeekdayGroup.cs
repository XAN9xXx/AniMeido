using AniMeido.Contracts.Models;
using System.Collections.ObjectModel;

namespace AniMeido.Plugin.Base.ViewModels
{
    public class WeekdayGroup
    {
        public string WeekdayName { get; set; } = "";
        public ObservableCollection<Anime> Items { get; set; } = [];
    }
}
