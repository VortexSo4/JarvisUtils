using System;
using System.Collections.Generic;
using System.Linq;

namespace JarvisUtils.Core;

public sealed class ModuleRunner(IJarvisServices services)
{
    private readonly List<IJarvisModule> _modules = [];
    public IReadOnlyList<IJarvisModule> Modules => _modules;

    public void Start(IEnumerable<IJarvisModule> modules)
    {
        foreach (var module in modules)
        {
            try
            {
                module.Initialize(services);
                _modules.Add(module);
                Log.Info($"Модуль {module.Name} запущен");
            }
            catch (Exception ex) { Log.Error($"Модуль {module.Name} не запустился: {ex}"); }
        }
    }

    public void Shutdown()
    {
        foreach (var module in _modules.AsEnumerable().Reverse())
        {
            try { module.Shutdown(); }
            catch (Exception ex) { Log.Error($"shutdown {module.Name}: {ex}"); }
        }
        _modules.Clear();
    }

    /// <summary>Приостанавливает все модули (например, пока открыто окно настроек).</summary>
    public void SuspendAll()
    {
        foreach (var m in _modules) m.Suspended = true;
        services.Hotkeys.Suspend();
    }

    public void ResumeAll()
    {
        foreach (var m in _modules) m.Suspended = false;
        services.Hotkeys.Resume();
    }
}