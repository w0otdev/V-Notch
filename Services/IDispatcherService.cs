namespace VNotch.Services;

public interface IDispatcherService
{

    void BeginInvoke(Action action);

    void BeginInvokeBackground(Action action) => BeginInvoke(action);

    void Invoke(Action action);

    bool CheckAccess();
}
