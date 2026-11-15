using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using Nova.TransferProtocol;

namespace Nova.TransferEngine;

/// <summary>Windows x64 adapter for the versioned Nova WebRTC native ABI.</summary>
public sealed class WebRtcPeer : IAsyncDisposable
{
    private const int MaxQueueMessages = 128;
    private const int MaxQueueBytes = 2 * 1024 * 1024;
    private const int MaxMessageBytes = 64 * 1024;
    private readonly SafePeerHandle _handle;
    private readonly Channel<PeerEvent> _events = Channel.CreateBounded<PeerEvent>(new BoundedChannelOptions(MaxQueueMessages) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Native.EventCallback _callback;
    private int _pendingBytes;

    private WebRtcPeer(SafePeerHandle handle)
    {
        _handle = handle;
        _callback = OnNativeEvent;
    }

    public static WebRtcPeer Create(IEnumerable<string> iceServers, bool relayOnly = false)
    {
        ArgumentNullException.ThrowIfNull(iceServers);
        var servers = iceServers.ToArray();
        if (servers.Length > 32 || servers.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("At most 32 non-empty ICE server URLs are supported.", nameof(iceServers));
        var peer = new WebRtcPeer(SafePeerHandle.Invalid);
        var utf8 = servers.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var pointers = Marshal.AllocHGlobal(IntPtr.Size * utf8.Length);
        try
        {
            for (var i = 0; i < utf8.Length; i++) Marshal.WriteIntPtr(pointers, i * IntPtr.Size, utf8[i]);
            var result = Native.nova_peer_create_ex(pointers, utf8.Length, MaxMessageBytes, relayOnly ? 1 : 0, peer._callback, IntPtr.Zero, out var id);
            Native.Check(result, "create peer");
            peer._handle.Initialize(id);
            peer._handle.KeepCallback(peer._callback);
            return peer;
        }
        catch { peer._events.Writer.TryComplete(); throw; }
        finally { foreach (var item in utf8) Marshal.FreeCoTaskMem(item); Marshal.FreeHGlobal(pointers); }
    }

    public async IAsyncEnumerable<PeerEvent> Events([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try { yield return item; }
            finally { Interlocked.Add(ref _pendingBytes, -item.Data.Length); }
        }
    }

    public void CreateReliableChannel(string label = "nova-transfer")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        Native.Check(Native.nova_peer_create_channel(_handle.Id, label, out var channel), "create data channel");
        _handle.ChannelId = channel;
    }

    public void CloseDataChannel()
    {
        if (_handle.IsClosed || _handle.IsInvalid) return;
        Native.Check(Native.nova_peer_close_channel(_handle.Id), "close data channel");
        _handle.ChannelId = -1;
    }

    public void CreateOffer() => Native.Check(Native.nova_peer_create_offer(_handle.Id), "create offer");
    public void CreateAnswer() => Native.Check(Native.nova_peer_create_answer(_handle.Id), "create answer");
    public void SetRemoteDescription(string sdp, string type) => Native.Check(Native.nova_peer_set_remote_description(_handle.Id, sdp, type), "set remote description");
    public void AddIceCandidate(string candidate, string? mid = null) => Native.Check(Native.nova_peer_add_candidate(_handle.Id, candidate, mid), "add ICE candidate");

    public SelectedCandidatePair? GetSelectedCandidatePair()
    {
        if (_handle.IsClosed || _handle.IsInvalid) return null;
        var local = new System.Text.StringBuilder(2048);
        var remote = new System.Text.StringBuilder(2048);
        var result = Native.nova_peer_get_selected_candidate_pair(_handle.Id, local, local.Capacity, remote, remote.Capacity);
        if (result < 0) return null;
        var localText = local.ToString();
        var remoteText = remote.ToString();
        return new SelectedCandidatePair(localText, remoteText,
            localText.Contains("typ relay", StringComparison.OrdinalIgnoreCase) || remoteText.Contains("typ relay", StringComparison.OrdinalIgnoreCase));
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        if (message.IsEmpty || message.Length > MaxMessageBytes) throw new ArgumentOutOfRangeException(nameof(message));
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copy = message.ToArray();
            var id = _handle.ChannelId;
            if (id < 0) throw new InvalidOperationException("DataChannel has not been created.");
            var result = Native.nova_peer_send(id, copy, copy.Length);
            if (result == -4) throw new IOException("DataChannel backpressure limit reached; retry after buffered amount drains.");
            Native.Check(result, "send DataChannel message");
        }
        finally { _sendGate.Release(); }
    }

    public ValueTask SendControlAsync<T>(TransferControlType type, string transferId, T payload, CancellationToken token = default) =>
        SendAsync(TransferProtocolCodec.EncodeControl(type, transferId, payload), token);

    private void OnNativeEvent(IntPtr user, int kind, IntPtr data, int length, IntPtr detail)
    {
        try
        {
            if (length < 0 || length > MaxMessageBytes || (length > 0 && data == IntPtr.Zero))
                throw new InvalidDataException($"Native callback supplied invalid event length {length}.");
            var bytes = length == 0 ? Array.Empty<byte>() : new byte[length];
            if (length > 0) Marshal.Copy(data, bytes, 0, length);
            var text = detail == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(detail);
            var ev = new PeerEvent(kind, bytes, text);
            if (kind == 7 && bytes.Length == sizeof(int))
                _handle.ChannelId = BitConverter.ToInt32(bytes, 0);
            var next = Interlocked.Add(ref _pendingBytes, bytes.Length);
            if (next > MaxQueueBytes || !_events.Writer.TryWrite(ev))
            {
                Interlocked.Add(ref _pendingBytes, -bytes.Length);
                _events.Writer.TryComplete(new IOException("Native event queue capacity exceeded; events were rejected."));
            }
        }
        catch (Exception ex) { _events.Writer.TryComplete(new IOException("Native callback could not be processed.", ex)); }
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        _handle.Dispose();
        _sendGate.Dispose();
        return ValueTask.CompletedTask;
    }

    public sealed record PeerEvent(int Kind, byte[] Data, string? Detail);
    public sealed record SelectedCandidatePair(string Local, string Remote, bool IsRelay);

    private sealed class SafePeerHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal static SafePeerHandle Invalid => new();
        internal int Id => checked((int)handle);
        internal int ChannelId = -1;
        private Native.EventCallback? _callbackRoot;
        internal void Initialize(int id) => SetHandle(new IntPtr(id));
        internal void KeepCallback(Native.EventCallback callback) => _callbackRoot = callback;
        private SafePeerHandle() : base(true) { }
        protected override bool ReleaseHandle() { Native.nova_peer_close(checked((int)handle)); return true; }
    }

    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void EventCallback(IntPtr user, int kind, IntPtr data, int length, IntPtr detail);
        [DllImport("nova_webrtc", CallingConvention = CallingConvention.Cdecl)] internal static extern int nova_peer_create(IntPtr ice, int count, int limit, EventCallback callback, IntPtr user, out int peer);
        [DllImport("nova_webrtc", CallingConvention = CallingConvention.Cdecl)] internal static extern int nova_peer_create_ex(IntPtr ice, int count, int limit, int relayOnly, EventCallback callback, IntPtr user, out int peer);
        [DllImport("nova_webrtc", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern int nova_peer_get_selected_candidate_pair(int peer, System.Text.StringBuilder local, int localSize, System.Text.StringBuilder remote, int remoteSize);
        [DllImport("nova_webrtc", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern int nova_peer_create_channel(int peer, [MarshalAs(UnmanagedType.LPUTF8Str)] string label, out int channel);
        [DllImport("nova_webrtc", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern int nova_peer_set_remote_description(int peer, [MarshalAs(UnmanagedType.LPUTF8Str)] string sdp, [MarshalAs(UnmanagedType.LPUTF8Str)] string type);
        [DllImport("nova_webrtc", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal static extern int nova_peer_add_candidate(int peer, [MarshalAs(UnmanagedType.LPUTF8Str)] string candidate, [MarshalAs(UnmanagedType.LPUTF8Str)] string? mid);
        [DllImport("nova_webrtc", CallingConvention = CallingConvention.Cdecl)] internal static extern int nova_peer_create_offer(int peer);
        [DllImport("nova_webrtc", CallingConvention = CallingConvention.Cdecl)] internal static extern int nova_peer_create_answer(int peer);
        [DllImport("nova_webrtc", CallingConvention = CallingConvention.Cdecl)] internal static extern int nova_peer_send(int channel, byte[] data, int length);
        [DllImport("nova_webrtc", CallingConvention = CallingConvention.Cdecl)] internal static extern int nova_peer_close_channel(int peer);
        [DllImport("nova_webrtc", CallingConvention = CallingConvention.Cdecl)] internal static extern int nova_peer_close(int peer);
        internal static void Check(int result, string operation) { if (result != 0) throw new IOException($"Native WebRTC {operation} failed ({result})."); }
    }
}
