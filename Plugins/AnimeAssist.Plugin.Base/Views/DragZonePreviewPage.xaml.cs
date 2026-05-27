using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class DragZonePreviewPage : Page
    {
        // 静态示例卡片集合（用整数填充，DataTemplate 不依赖数据上下文）
        public List<int> _sampleCards { get; } = new() { 1, 2, 3, 4 };

        public DragZonePreviewPage()
        {
            InitializeComponent();
        }
    }
}
