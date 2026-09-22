using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Text;

namespace AssistQuestEditor.App;

/// <summary>
/// Гарантирует работу приложения в единственном экземпляре и передаёт путь
/// открываемого файла уже запущенному экземпляру.
///
/// Зачем: двойной клик по .aqquest/.aqscene (или Enter в проводнике) запускает
/// новый процесс. Без этого механизма каждый такой запуск поднимал бы второе
/// окно, и пользователь терял бы текущее состояние редактора.
///
/// Порядок для нового процесса:
///   1) попытаться захватить мьютекс без ожидания — обычный случай «я первый»;
///   2) если занят, передать путь в именованный канал работающему экземпляру
///      и завершиться;
///   3) если канал недоступен (первый экземпляр как раз закрывается), ещё раз
///      попытаться захватить мьютекс и стать первым.
///
/// Имена — в пространстве <c>Local\</c>: изоляция по сессии Windows. Имя
/// пользователя в имя мьютекса не добавляется: доменные учётки содержат «\»,
/// что сделало бы имя невалидным.
///
/// Любой сбой передачи не блокирует работу: причина попадает в журнал.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string ResourceName = "AssistQuestEditor.SingleInstance";

    /// <summary>Ожидание ответа работающего экземпляра, мс. Коротко: путь короткий.</summary>
    private const int SendTimeoutMs = 1500;

    /// <summary>Ожидание мьютекса на шаге 3, мс.</summary>
    private const int RetryAcquireTimeoutMs = 2000;

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;

    private SingleInstanceGuard(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>
    /// Путь, присланный другим экземпляром приложения. Пустая строка означает
    /// повторный запуск без файла — работающее окно нужно просто показать.
    /// </summary>
    public event EventHandler<string>? ActivationRequested;

    /// <summary>
    /// Имена для текущей сессии Windows.
    ///
    /// SessionId включён намеренно: при быстром переключении пользователей две
    /// сессии должны иметь и разные мьютексы, и разные каналы. Иначе вторая
    /// сессия не смогла бы поднять сервер канала с тем же именем, а второй
    /// экземпляр молча не открылся бы.
    ///
    /// Имя канала без префикса <c>Local\</c>: для каналов такого пространства имён
    /// нет, разделение обеспечивает SessionId. Мьютекс, наоборот, использует
    /// <c>Local\</c> — там это реальное пространство имён.
    /// </summary>
    private static (string MutexName, string PipeName) ResolveNames()
    {
        var session = Process.GetCurrentProcess().SessionId;
        return ($"Local\\{ResourceName}.{session}", $"{ResourceName}.{session}");
    }

    /// <summary>
    /// Пытается стать единственным экземпляром.
    ///
    /// Возвращает <c>true</c> и готовый guard, если этот процесс — первый.
    /// Возвращает <c>false</c>, если приложение уже работает: путь (при наличии)
    /// отправлен ему, и текущему процессу остаётся только завершиться.
    /// </summary>
    public static bool TryAcquire(string? startupPath, [NotNullWhen(true)] out SingleInstanceGuard? guard)
    {
        guard = null;

        var (mutexName, pipeName) = ResolveNames();
        var mutex = new Mutex(initiallyOwned: false, mutexName);

        // Шаг 1: без ожидания. Задержка на запуске недопустима, поэтому обычный
        // случай не должен ждать даже миллисекунду.
        if (TryEnter(mutex))
        {
            guard = new SingleInstanceGuard(mutex, pipeName);
            AppLogger.Info("Single instance: экземпляр первый.", $"mutex={mutexName}");
            return true;
        }

        AppLogger.Info("Single instance: приложение уже запущено, передаю запрос.", $"mutex={mutexName}");

        // Шаг 2: передать путь работающему экземпляру.
        if (TryForward(pipeName, startupPath))
        {
            mutex.Dispose();
            return false;
        }

        // Шаг 3: канала нет — прежний экземпляр, вероятно, уже завершается.
        // Ждём освобождения мьютекса и становимся первыми.
        try
        {
            if (mutex.WaitOne(RetryAcquireTimeoutMs, exitContext: false))
            {
                guard = new SingleInstanceGuard(mutex, pipeName);
                AppLogger.Info("Single instance: прежний экземпляр завершился, продолжаю как первый.", $"mutex={mutexName}");
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            // Прежний экземпляр упал, не освободив мьютекс. Владение перешло к нам.
            guard = new SingleInstanceGuard(mutex, pipeName);
            AppLogger.Warn("Single instance: прежний экземпляр завершился аварийно; продолжаю как первый.");
            return true;
        }

        // Экземпляр работает, но канал недоступен: второй запуск не должен
        // открывать второе окно, поэтому просто завершаемся.
        AppLogger.Warn("Single instance: запрос передать не удалось, второй экземпляр не запускается.");
        mutex.Dispose();
        return false;
    }

    private static bool TryEnter(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // Владение перешло к нам после аварийного завершения прежнего процесса.
            return true;
        }
    }

    private static bool TryForward(string pipeName, string? path)
    {
        var payload = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim();

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.None);

            client.Connect(SendTimeoutMs);

            // Кириллица в путях обязательна: имена файлов здесь русские.
            var bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();

            AppLogger.Info("Single instance: запрос передан работающему экземпляру.", $"path={payload}");
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn("Single instance: канал недоступен.", ex.Message);
            return false;
        }
    }

    /// <summary>Начинает слушать запросы от последующих запусков.</summary>
    public void StartListening() =>
        _listener = Task.Run(ListenLoopAsync);

    private async Task ListenLoopAsync()
    {
        var token = _shutdown.Token;

        // Сервер создаётся один раз и переиспользуется. Пересоздание на каждой
        // итерации оставляло бы окно, когда клиент уже соединился, а сервера ещё
        // нет: второй запуск в этот момент не смог бы передать путь.
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                while (!token.IsCancellationRequested)
                {
                    await server.WaitForConnectionAsync(token);

                    var buffer = new byte[8192];
                    var read = await server.ReadAsync(buffer, token);
                    var payload = Encoding.UTF8.GetString(buffer, 0, read);

                    AppLogger.Info("Single instance: получен запрос открытия.", $"path={payload}");
                    ActivationRequested?.Invoke(this, payload);

                    // Один сервер обслуживает одно соединение за раз, поэтому
                    // после обработки нужно отключить клиента и ждать следующий.
                    server.Disconnect();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Сбой не должен убивать слушателя: следующая итерация создаст
                // новый сервер, иначе приложение перестанет принимать активации.
                AppLogger.Warn("Single instance: сбой канала, повтор.", ex.Message);
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();

        try
        {
            _listener?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException)
        {
            // Слушатель уже завершился — это ожидаемо при остановке приложения.
        }

        _shutdown.Dispose();

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Мьютекс уже освобождён или не принадлежит нам.
        }

        _mutex.Dispose();
    }
}
