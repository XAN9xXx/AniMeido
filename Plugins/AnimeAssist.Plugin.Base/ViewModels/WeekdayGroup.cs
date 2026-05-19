using AniMeido.Contracts.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AniMeido.Plugin.Base.ViewModels
{
    public class WeekdayGroup
    {
        public string WeekdayName { get; set; } = "";
        public ObservableCollection<Anime> Items { get; set; } = [];
    }
}
