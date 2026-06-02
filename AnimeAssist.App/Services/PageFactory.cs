using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App.Services
{
    /// <summary>
    /// 页面工厂：从 DI 容器创建页面实例，替代反射 + 字符串导航。
    /// </summary>
    public class PageFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public PageFactory(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// 创建指定类型的页面实例。
        /// </summary>
        public Page CreatePage(Type pageType)
        {
            return (Page)_serviceProvider.GetRequiredService(pageType);
        }
    }
}