using VNotch.ApplicationServices;
using VNotch.Contracts;
using VNotch.Services;

namespace VNotch.Modules;

public abstract class NotchModuleBase : ModuleLifecycle
{
    protected NotchModuleBase(IModuleScheduler? scheduler = null)
        : base(scheduler ?? UiModuleScheduler.Instance, (category, message) => RuntimeLog.Log(category, message)) { }
}
