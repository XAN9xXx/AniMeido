using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class DragZonePreviewPage : Page
    {
        // 静态示例卡片集合
        public List<object> _sampleCard { get; } = new() { new(), new(), new() };

        public DragZonePreviewPage()
        {
            InitializeComponent();
        }
    }
}
