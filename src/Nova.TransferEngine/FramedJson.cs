using System.Buffers.Binary;
using System.Text.Json;

namespace Nova.TransferEngine;

public static class FramedJson
{
    public const int MaximumFrameLength = 64 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length == 0 || payload.Length > MaximumFrameLength)
            throw new InvalidDataException("Размер сообщения протокола превышает допустимый предел.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaximumFrameLength)
            throw new InvalidDataException("Недопустимый размер сообщения протокола.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Пустое сообщение протокола.");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}

internal sealed record WireEnvelope(int Version, string Type, JsonElement Payload)
{
    public const int CurrentVersion = 1;
    public static WireEnvelope Create<T>(string type, T payload) => new(CurrentVersion, type, JsonSerializer.SerializeToElement(payload));
    public void EnsureVersion() { if (Version != CurrentVersion) throw new InvalidDataException("Версия протокола не поддерживается."); }
    public T ReadPayload<T>() => Payload.Deserialize<T>() ?? throw new InvalidDataException("Сообщение протокола не содержит ожидаемых данных.");
}

internal sealed record WireReply(bool Accepted, string? Message = null);
