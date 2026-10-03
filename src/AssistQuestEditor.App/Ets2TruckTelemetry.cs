using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AssistQuestEditor.App;

/// <summary>
/// Лёгкий read-only клиент TruckTel для симулятора AQE.
///
/// Источник тот же, что используется ETS2 Assist: WebSocket delta + REST-снимок
/// <c>/api/rest/flat/truck</c>. Клиент запускается только когда пользователь
/// включил «Телеметрия», поэтому сам по себе симулятор не держит сетевой канал.
/// </summary>
public sealed class Ets2TruckTelemetry : IDisposable
{
    private const int DefaultPort = 8080;
    private const int StaleMilliseconds = 3000;
    private const int RestIntervalMilliseconds = 1000;

    public sealed record Snapshot(
        double X,
        double Y,
        double Z,
        double HeadingDegrees,
        double SpeedKmh,
        bool Live,
        DateTimeOffset SampleAt);

    private readonly object _sync = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly int _configuredPort;

    private CancellationTokenSource? _cts;
    private ClientWebSocket? _socket;
    private Task? _wsTask;
    private Task? _restTask;
    private Snapshot? _lastSnapshot;
    private bool _running;
    private int _port;
    private DateTimeOffset _nextWarningUtc;

    public Ets2TruckTelemetry(int port = DefaultPort)
    {
        _configuredPort = port > 0 ? port : DefaultPort;
        _port = _configuredPort;
    }

    public bool Running
    {
        get
        {
            lock (_sync)
                return _running;
        }
    }

    public int Port
    {
        get
        {
            lock (_sync)
                return _port;
        }
    }

    public bool TryGetSnapshot(out Snapshot snapshot)
    {
        lock (_sync)
        {
            if (_lastSnapshot is null)
            {
                snapshot = default!;
                return false;
            }

            var live = DateTimeOffset.UtcNow - _lastSnapshot.SampleAt <=
                       TimeSpan.FromMilliseconds(StaleMilliseconds);

            snapshot = _lastSnapshot with { Live = live };
            return true;
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_running)
                return;

            _running = true;
            _cts = new CancellationTokenSource();
        }

        _wsTask = ConnectWebSocketLoopAsync(_cts.Token);
        _restTask = RestLoopAsync(_cts.Token);
        AppLogger.Info("Ets2TruckTelemetry: фид телеметрии запущен.", $"port={_port}");
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        ClientWebSocket? socket;

        lock (_sync)
        {
            if (!_running)
                return;

            _running = false;
            cts = _cts;
            socket = _socket;
            _cts = null;
            _socket = null;
        }

        try { cts?.Cancel(); } catch { }
        try { socket?.Abort(); socket?.Dispose(); } catch { }
        try { cts?.Dispose(); } catch { }

        AppLogger.Info("Ets2TruckTelemetry: фид телеметрии остановлен.");
    }

    private async Task ConnectWebSocketLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            ClientWebSocket? socket = null;
            try
            {
                var port = ResolvePort();
                lock (_sync) _port = port;

                socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

                await socket.ConnectAsync(
                    new Uri($"ws://localhost:{port}/api/ws/delta/flat/?throttle=50"),
                    token);

                lock (_sync) _socket = socket;
                await ReceiveWebSocketAsync(socket, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LogUnavailable("WS", ex);
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_socket, socket))
                        _socket = null;
                }

                try { socket?.Dispose(); } catch { }
            }

            try
            {
                await Task.Delay(2500, token);
            }
            catch
            {
                return;
            }
        }
    }

    private async Task ReceiveWebSocketAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[32768];

        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                if (result.Count > 0)
                    message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (message.Length == 0)
                continue;

            try
            {
                using var document = JsonDocument.Parse(message.GetBuffer().AsSpan(0, checked((int)message.Length)));
                Apply(document.RootElement);
            }
            catch (JsonException ex)
            {
                AppLogger.Warn(
                    "Ets2TruckTelemetry: некорректный кадр WS TruckTel.",
                    ex.Message);
            }
        }
    }

    private async Task RestLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var port = ResolvePort();
                lock (_sync) _port = port;

                using var response = await _http.GetAsync(
                    $"http://localhost:{port}/api/rest/flat/truck",
                    token);

                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                Apply(document.RootElement);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // REST — резервный канал. Не превращаем отсутствие TruckTel в
                // поток одинаковых ошибок, но оставляем запись в логах для
                // диагностики подключения.
                LogUnavailable("REST", ex);
            }

            try
            {
                await Task.Delay(RestIntervalMilliseconds, token);
            }
            catch
            {
                return;
            }
        }
    }

    private void Apply(JsonElement root)
    {
        if (!root.TryGetProperty("truck.world.placement", out var placement) ||
            placement.ValueKind != JsonValueKind.Array ||
            placement.GetArrayLength() < 3)
        {
            return;
        }

        if (!TryNumber(placement[0], out var x) ||
            !TryNumber(placement[1], out var y) ||
            !TryNumber(placement[2], out var z))
        {
            return;
        }

        var headingFraction = placement.GetArrayLength() >= 4 &&
                              TryNumber(placement[3], out var h)
            ? h
            : 0d;

        var speedKmh = 0d;
        if (root.TryGetProperty("truck.speed", out var speed) &&
            TryNumber(speed, out var speedMs))
        {
            speedKmh = Math.Abs(speedMs) * 3.6d;
        }

        var headingDegrees = NormalizeDegrees(headingFraction * 360d);
        var now = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            _lastSnapshot = new Snapshot(
                x,
                y,
                z,
                headingDegrees,
                speedKmh,
                true,
                now);
        }
    }

    private void LogUnavailable(string channel, Exception ex)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            if (now < _nextWarningUtc)
                return;
            _nextWarningUtc = now.AddSeconds(15);
        }

        AppLogger.Warn(
            "Ets2TruckTelemetry: TruckTel недоступен.",
            $"channel={channel}; port={Port}; error={ex.GetBaseException().Message}");
    }

    private int ResolvePort()
    {
        return _configuredPort;
    }

    private static bool TryNumber(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out value) &&
            double.IsFinite(value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static double NormalizeDegrees(double value)
    {
        if (!double.IsFinite(value))
            return 0d;

        var normalized = value % 360d;
        return normalized < 0d ? normalized + 360d : normalized;
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
