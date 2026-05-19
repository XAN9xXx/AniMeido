using AniMeido.Contracts;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class AnimeDetailPage : Page
    {
        public AnimeDetailViewModel ViewModel { get; }

        public AnimeDetailPage()
        {
            var ds = AppServices.Provider!.GetRequiredService<IAnimeDataSource>();
            ViewModel = new AnimeDetailViewModel(ds);
            DataContext = ViewModel;
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if(e.Parameter is int animeID)
                ViewModel.LoadDetailCommand.Execute(animeID);
        }
    }
}
