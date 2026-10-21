using Microsoft.Extensions.DependencyInjection;

namespace VTC.Client;

/// <summary>
/// 轻量服务定位器（DI 容器句柄 + 解析）。
/// 规划 §4.1 用 DryIoc；P0 用 Microsoft.Extensions.DependencyInjection（等价容器），
/// 应用级单例服务集中注册，视图/VM 通过 AppServiceLocator 解析。
/// </summary>
public static class AppServiceLocator
{
    public static IServiceProvider Services { get; private set; } = null!;

    public static void Initialize(IServiceCollection services)
    {
        Services = services.BuildServiceProvider();
    }

    public static T Get<T>() where T : class => Services.GetService<T>()
        ?? throw new InvalidOperationException($"未注册服务：{typeof(T).Name}");
}
