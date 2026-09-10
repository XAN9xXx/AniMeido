using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App.Services
{
    /// <summary>
    /// 页面工厂：从 DI 容器创建页面实例，替代反射 + 字符串导航。
    /// 使用 ActivatorUtilities.CreateInstance 按需注入构造函数依赖，
    /// 不再要求页面类型提前注册到 DI 容器。
    /// </summary>
    public sealed class PageFactory
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
            ArgumentNullException.ThrowIfNull(pageType);

            if (!typeof(Page).IsAssignableFrom(pageType))
                throw new InvalidOperationException($"{pageType.FullName} is not a WinUI Page.");

            return (Page)ActivatorUtilities.CreateInstance(_serviceProvider, pageType);
        }
    }
}