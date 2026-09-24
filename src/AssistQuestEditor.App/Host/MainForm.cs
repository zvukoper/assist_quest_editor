        // Индикатор «Симулятор: активен» зависит от запущенной симуляции, а не от
        // открытого окна, поэтому состояние подписывается на те же события
        // Runtime, что видит окно симулятора.
        _runtime.Published += Runtime_Published;

        // История последних открытых ресурсов: список читается здесь один раз и
        // дальше живёт в памяти, чтобы клик по нему не ходил на диск.
        _recentQuestFiles = RecentFileList.FromPaths(
            AppUiPreferencesStore.LoadRecentFiles(RecentFileKind.Quest));
        _recentSceneFiles = RecentFileList.FromPaths(
            AppUiPreferencesStore.LoadRecentFiles(RecentFileKind.Scene));
        PruneRecentFiles();
        _recentQuestFiles.Changed += RecentQuestFiles_Changed;
        _recentSceneFiles.Changed += RecentSceneFiles_Changed;

        // Сначала закрывается Simulator: его FormClosing выполняет автосохранение текущего
        // прохождения. Только после этого можно освобождать Runtime и Director.
        FormClosing += (_, _) => _simulator?.Close();

        BrowserReady += MainForm_BrowserReady;
        FormClosed += (_, _) =>
        {
            BrowserReady -= MainForm_BrowserReady;
            _sceneRuntime.Published -= SceneRuntime_Published;
            _runtime.Published -= Runtime_Published;
            _sceneCatalog.Changed -= SceneCatalog_Changed;
            _recentQuestFiles.Changed -= RecentQuestFiles_Changed;
            _recentSceneFiles.Changed -= RecentSceneFiles_Changed;

            foreach (var editor in _editors.ToArray())
            {
                editor.Close();
            }

            _settings?.Close();
            _dynamicEventDirector.Dispose();
            _runtime.Dispose();
            _junctionReview?.Close();
            _cityBoundaryForm?.Close();
        };
    }

    /// <summary>
    /// Открывает окно ручной проверки перекрёстков (одиночный экземпляр).
    ///
    /// Одно окно на приложение: проверка — это состояние (исключённые и
    /// добавленные узлы), и два окна показывали бы его по-разному.
    /// </summary>
    private void OpenJunctionReview()
    {
        if (_junctionReview is not null && !_junctionReview.IsDisposed)
        {
            _junctionReview.WindowState = FormWindowState.Normal;
            _junctionReview.BringToFront();
            _junctionReview.Activate();
            return;
        }

        _junctionReview = new JunctionReviewForm(_roads, _junctions);
        _junctionReview.FormClosed += (_, _) => _junctionReview = null;
        _junctionReview.Show(this);
    }

    /// <summary>