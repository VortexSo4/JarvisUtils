using System;
using System.Collections.Generic;

namespace JarvisUtils.Core;

public sealed class TrayMenuRegistry : ITrayMenuBuilder
{
    private readonly List<TrayEntry> _entries = [];
    public IReadOnlyList<TrayEntry> Entries => _entries;

    public void AddItem(string header, Action onClick) => _entries.Add(new TrayButtonItem(header, onClick));
    public void AddToggle(string header, Func<bool> get, Action<bool> set) => _entries.Add(new TrayToggleEntry(header, get, set));
    public void AddSeparator() => _entries.Add(new TraySeparatorEntry());
}