using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App.Services
{
    /// <summary>
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

        public Page CreatePage(Type pageType)
        {
            ArgumentNullException.ThrowIfNull(pageType);

            if (!typeof(Page).IsAssignableFrom(pageType))
                throw new InvalidOperationException($"{pageType.FullName} is not a WinUI Page.");

            return (Page)ActivatorUtilities.CreateInstance(_serviceProvider, pageType);
        }
    }
}