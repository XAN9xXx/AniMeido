namespace AniMeido.Plugin.Base.ViewModels;

/// <summary>拥有今天页一次弹窗操作，覆盖显示前的读取和确认后的保存。</summary>
internal sealed class TodayDialogOperation
{
    private bool _isActive;

    public IDisposable? TryBegin()
    {
        if (_isActive)
            return null;

        _isActive = true;
        return new Admission(this);
    }

    private sealed class Admission(TodayDialogOperation owner) : IDisposable
    {
        private TodayDialogOperation? _owner = owner;

        public void Dispose()
        {
            if (_owner is null)
                return;

            _owner._isActive = false;
            _owner = null;
        }
    }
}
