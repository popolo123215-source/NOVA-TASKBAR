using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nova.WindowsClient.Services;

internal sealed record ObsSnapshot(
    string Version,
    string CurrentScene,
    IReadOnlyList<string> Scenes,
    bool IsRecording,
    string RecordingState);

internal sealed class ObsWebSocketClient : IAsyncDisposable
{
    private const int MaximumMessageBytes = 1024 * 1024;
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private Task? _receiveLoop;
    private int _nextRequestId;
    private int _disposed;

    private ObsWebSocketClient()
    {
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    public event Action<string>? Disconnected;
    public bool IsConnected => _socket.State == WebSocketState.Open;

    public static async Task<ObsWebSocketClient> ConnectAsync(string password, int port, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Введите пароль OBS WebSocket. NOVA его не сохраняет.", nameof(password));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Порт должен быть в диапазоне 1–65535.");

        var client = new ObsWebSocketClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await client._socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), timeout.Token);

            using var hello = await client.ReadMessageAsync(timeout.Token);
            var helloRoot = hello.RootElement;
            if (helloRoot.GetProperty("op").GetInt32() != 0)
                throw new InvalidDataException("OBS WebSocket прислал некорректное приветствие.");

            var helloData = helloRoot.GetProperty("d");
            var rpcVersion = helloData.GetProperty("rpcVersion").GetInt32();
            if (!helloData.TryGetProperty("authentication", out var authData))
                throw new InvalidDataException("Включите парольную аутентификацию OBS WebSocket перед подключением.");
            var salt = authData.GetProperty("salt").GetString();
            var challenge = authData.GetProperty("challenge").GetString();
            if (string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(challenge))
                throw new InvalidDataException("OBS WebSocket прислал неполные данные аутентификации.");
            var authentication = CreateAuthentication(password, salt, challenge);

            await client.SendJsonAsync(new
            {
                op = 1,
                d = new { rpcVersion, authentication, eventSubscriptions = 0 }
            }, timeout.Token);

            using var identified = await client.ReadMessageAsync(timeout.Token);
            if (identified.RootElement.GetProperty("op").GetInt32() != 2)
            {
                var reason = identified.RootElement.TryGetProperty("d", out var details) &&
                    details.TryGetProperty("message", out var message)
                    ? message.GetString()
                    : null;
                throw new UnauthorizedAccessException(reason ?? "OBS отклонил WebSocket-аутентификацию.");
            }

            client._receiveLoop = client.ReceiveLoopAsync(client._lifetime.Token);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    internal static string CreateAuthentication(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    public async Task<ObsSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var version = await RequestAsync("GetVersion", null, cancellationToken);
        var recording = await RequestAsync("GetRecordStatus", null, cancellationToken);
        var currentScene = await RequestAsync("GetCurrentProgramScene", null, cancellationToken);
        var scenes = await RequestAsync("GetSceneList", null, cancellationToken);
        var sceneNames = scenes.GetProperty("scenes").EnumerateArray()
            .Select(scene => scene.GetProperty("sceneName").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();

        return new ObsSnapshot(
            version.GetProperty("obsVersion").GetString() ?? "OBS",
            currentScene.GetProperty("currentProgramSceneName").GetString() ?? "",
            sceneNames,
            recording.GetProperty("outputActive").GetBoolean(),
            recording.TryGetProperty("outputState", out var state) ? state.GetString() ?? "" : "");
    }

    public Task<JsonElement> StartRecordingAsync(CancellationToken cancellationToken) =>
        RequestAsync("StartRecord", null, cancellationToken);

    public Task<JsonElement> StopRecordingAsync(CancellationToken cancellationToken) =>
        RequestAsync("StopRecord", null, cancellationToken);

    public Task<JsonElement> SetProgramSceneAsync(string sceneName, CancellationToken cancellationToken) =>
        RequestAsync("SetCurrentProgramScene", new { sceneName }, cancellationToken);

    private async Task<JsonElement> RequestAsync(string requestType, object? requestData, CancellationToken cancellationToken)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Соединение с OBS WebSocket закрыто.");

        var requestId = Interlocked.Increment(ref _nextRequestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(requestId, completion))
            throw new InvalidOperationException("Не удалось зарегистрировать запрос OBS.");

        try
        {
            await SendJsonAsync(new
            {
                op = 6,
                d = new { requestType, requestId, requestData }
            }, cancellationToken);

            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            return response;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private async Task SendJsonAsync<T>(T message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task<JsonDocument> ReadMessageAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(chunk), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("OBS закрыл WebSocket-соединение.");
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("OBS WebSocket прислал сообщение неподдерживаемого типа.");
            if (buffer.Length + result.Count > MaximumMessageBytes)
                throw new InvalidDataException("Ответ OBS WebSocket превышает допустимый размер.");
            buffer.Write(chunk, 0, result.Count);
        } while (!result.EndOfMessage);

        return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        string? disconnectReason = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var document = await ReadMessageAsync(cancellationToken);
                var root = document.RootElement;
                if (!root.TryGetProperty("op", out var op) || op.GetInt32() != 7 ||
                    !root.TryGetProperty("d", out var data) ||
                    !data.TryGetProperty("requestId", out var requestIdElement))
                    continue;

                var requestId = requestIdElement.GetString();
                if (requestId is null || !_pendingRequests.TryGetValue(requestId, out var completion))
                    continue;

                var status = data.GetProperty("requestStatus");
                if (status.GetProperty("result").GetBoolean())
                {
                    JsonElement response;
                    if (data.TryGetProperty("responseData", out var responseData))
                    {
                        response = responseData.Clone();
                    }
                    else
                    {
                        using var emptyResponse = JsonDocument.Parse("{}");
                        response = emptyResponse.RootElement.Clone();
                    }
                    completion.TrySetResult(response);
                }
                else
                {
                    var comment = status.TryGetProperty("comment", out var commentElement)
                        ? commentElement.GetString()
                        : null;
                    completion.TrySetException(new InvalidOperationException(
                        comment ?? $"OBS отклонил запрос «{data.GetProperty("requestType").GetString()}»."));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or ObjectDisposedException or InvalidOperationException or KeyNotFoundException)
        {
            disconnectReason = ex.Message;
        }
        finally
        {
            var reason = disconnectReason ?? "Соединение с OBS WebSocket закрыто.";
            foreach (var request in _pendingRequests.Values)
                request.TrySetException(new IOException(reason));
            if (disconnectReason is not null && Volatile.Read(ref _disposed) == 0)
                Disconnected?.Invoke(reason);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "NOVA disconnect", timeout.Token); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { _socket.Abort(); }
        }

        if (_receiveLoop is not null)
        {
            var completed = await Task.WhenAny(_receiveLoop, Task.Delay(TimeSpan.FromSeconds(2)));
            if (!ReferenceEquals(completed, _receiveLoop))
                _socket.Abort();
            _lifetime.Cancel();
            await _receiveLoop;
        }
        _socket.Dispose();
        _sendGate.Dispose();
        _lifetime.Dispose();
    }
}
