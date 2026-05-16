using Microsoft.UI.Xaml.Controls;
using AnimeAssist.Contracts.Models;

namespace AnimeAssist.Plugin.Base.Views
{
    public sealed partial class CurrentSeasonPage : Page
    {
        public CurrentSeasonPage()
        {
            InitializeComponent();

            var cvs = new List<VoiceActor> 
            {
                new VoiceActor (1, "声优1", "https://example.com/voiceactor1.jpg"),
                new VoiceActor(2, "声优2", "https://example.com/voiceactor2.jpg"),
                new VoiceActor(3, "声优3", "https://example.com/voiceactor3.jpg")
            };
            var tags = new List<Tag>
            {
                new Tag("Tag1"),
                new Tag("Tag2"),
                new Tag("Tag3")
            };

            var anime1 = new Anime(1, "Title", "Studio", cvs, new DateOnly(2026, 5, 10), "CoverURL", "Description", 2026, 4);
            var anime2 = new Anime(2, "Title", "Studio", cvs, new DateOnly(2026, 5, 10), "CoverURL", "Description", 2026, 4);

            BasicGridView.ItemsSource = new List<Anime>
            {
                anime1,
                anime2
            };
            /*
             * 测试用例，用于验证BasicGridView是否正确显示Anime对象的列表
             */
        }
    }
}
