using System;
using System.Threading.Tasks;
using System.Windows;
using JarvisUtils.UI;
using Velopack;
using Velopack.Sources;

namespace JarvisUtils.Core;

/// <summary>
/// Автообновление через GitHub Releases (Velopack). Проверка — один раз при запуске, в фоне:
/// нашлась новая версия → скачиваем → предлагаем перезапуститься. «Позже» — обновление
/// поставится само при следующем выходе из приложения.
/// </summary>
public static class AppUpdater
{
    public const string RepoUrl = "https://github.com/VortexSo4/JarvisUtils";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(4); // не мешаем старту модулей

    /// <summary>Версия установленного приложения (null — запуск из IDE/bin, не через установщик).</summary>
    public static string? CurrentVersion { get; private set; }

    public static async Task CheckOnStartupAsync()
    {
        try
        {
            var manager = CreateManager();
            if (!manager.IsInstalled) return; // запуск из папки сборки — обновлять нечего
            CurrentVersion = manager.CurrentVersion?.ToString();

            await Task.Delay(StartupDelay);
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update == null) return;

            var version = update.TargetFullRelease.Version.ToString();
            Log.Info($"обновление: найдена версия {version} (текущая {CurrentVersion}), загрузка");
            await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);

            Application.Current.Dispatcher.Invoke(() =>
            {
                var prompt = new UpdatePromptWindow(version);
                prompt.RestartRequested += () =>
                {
                    Log.Info($"обновление: перезапуск в {version}");
                    Log.Flush();
                    manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
                };
                prompt.Postponed += () =>
                {
                    // поставится, когда приложение закроют (выход из трея, перезагрузка ПК)
                    manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
                    Log.Info($"обновление {version} отложено до выхода");
                };
                prompt.Show();
            });
        }
        catch (Exception ex)
        {
            // нет сети, GitHub недоступен, лимит API — не повод беспокоить пользователя
            Log.Warn($"обновление: проверка не удалась: {ex.Message}");
        }
    }

    private static UpdateManager CreateManager()
    {
        // для локальной проверки обновлений: папка с релизами вместо GitHub
        var local = Environment.GetEnvironmentVariable("JARVIS_UPDATE_SOURCE");
        IUpdateSource source = string.IsNullOrEmpty(local)
            ? new GithubSource(RepoUrl, accessToken: null, prerelease: false)
            : new SimpleFileSource(new System.IO.DirectoryInfo(local));
        return new UpdateManager(source);
    }
}
