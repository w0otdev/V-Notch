namespace VNotch.Services;

public interface IStartupManager
{
    bool IsAutoStartEnabled();
    void SetAutoStart(bool enable);
}
