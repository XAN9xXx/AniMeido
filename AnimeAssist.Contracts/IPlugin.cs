using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnimeAssist.Contracts
{
    public record PluginNavigationItem(string Label, string Icon, string PageTypeName);
        /*
        * Label: 显示在导航栏的名称
        * Icon: 显示在导航栏的图标 
        * PageTypeName: 导航到页面的类型名称
        */
    public interface IPlugin
    {
        string PID { get; }
        string DisplayName { get; }
        string Version { get; }
        bool IsRequired { get; }
        Task InitializeAsync(IServiceCollection services);
        IEnumerable<PluginNavigationItem>  GetNavigationItems();
    }
        /*
        * PID: 插件的唯一标识符
        * DisplayName: 插件的显示名称
        * Version: 插件的版本
        * isRequired: 是否为必需插件，必需插件无法卸载
        * InitializeAsync: 插件的初始化方法，接受一个ServiceCollection参数用于注册依赖项
        * GetNavigationItems: 获取插件提供的导航项列表，每个导航项包含标题、图标和页面类型名称
        */
}

