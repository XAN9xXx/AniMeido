using Xunit;

namespace AniMeido.Updates.Tests;

internal sealed class WindowsFileSharingFactAttribute : FactAttribute
{
    public WindowsFileSharingFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "本测试需要 Windows 文件共享锁的读取、移动或删除限制。";
        }
    }
}

internal sealed class WindowsFileSharingTheoryAttribute : TheoryAttribute
{
    public WindowsFileSharingTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "本测试需要 Windows 文件共享锁的读取、移动或删除限制。";
        }
    }
}
