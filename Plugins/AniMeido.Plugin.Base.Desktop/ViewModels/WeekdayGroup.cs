using AniMeido.Contracts.Models;
using System.Collections.ObjectModel;

namespace AniMeido.Plugin.Base.ViewModels
{
    public class WeekdayGroup
    {
        public int? Weekday { get; set; }
        public string WeekdayName { get; set; } = "";
        public ObservableCollection<Anime> Items { get; set; } = [];
    }
}
