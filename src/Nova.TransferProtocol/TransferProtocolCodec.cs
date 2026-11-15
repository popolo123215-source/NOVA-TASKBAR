using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace Nova.TransferProtocol;

public enum TransferControlType
{
    Offer,
    Decision,
    DeviceProof,
    Resume,
    BlockAcknowledgement,
    Complete,
    Failure,
    Cancel
}

public sealed record TransferControlMessage<T>(int ProtocolVersion, TransferControlType Type, string TransferId, T Payload);

public readonly record struct TransferDataBlock(Guid TransferId, long OffsetBytes, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Versioned NOVA transfer messages carried as individual WebRTC DataChannel messages.
/// Payloads are bounded before serialization; file bytes use the compact binary block format.
/// </summary>
public static class TransferProtocolCodec
{
    public const int CurrentVersion = 1;
    public const int MaximumMessageLength = 16 * 1024;
    public const int DataBlockHeaderLength = 36;
    public const int MaximumDataBlockLength = MaximumMessageLength - DataBlockHeaderLength;

    private static ReadOnlySpan<byte> DataMagic => "NVAF"u8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };

    public static byte[] EncodeControl<T>(TransferControlType type, string transferId, T payload)
    {
        ValidateTransferId(transferId);
        if (!IsKnownType(type)) throw new ArgumentOutOfRangeException(nameof(type));
        ArgumentNullException.ThrowIfNull(payload);

        var writer = new FixedBufferWriter(MaximumMessageLength);
        using (var json = new Utf8JsonWriter(writer))
        {
            json.WriteStartObject();
            json.WriteNumber("version", CurrentVersion);
            json.WriteString("type", TypeName(type));
            json.WriteString("transferId", transferId);
            json.WritePropertyName("payload");
            JsonSerializer.Serialize(json, payload, JsonOptions);
            json.WriteEndObject();
        }
        return writer.ToArray();
    }

    public static TransferControlMessage<T> DecodeControl<T>(ReadOnlyMemory<byte> message)
    {
        if (message.Length is 0 or > MaximumMessageLength)
            throw new InvalidDataException("Размер управляющего сообщения NOVA недопустим.");

        try
        {
            using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            EnsureNoDuplicateProperties(root);
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var versionElement) ||
                !root.TryGetProperty("type", out var typeElement) ||
                !root.TryGetProperty("transferId", out var transferIdElement) ||
                !root.TryGetProperty("payload", out var payloadElement))
                throw new InvalidDataException("Управляющее сообщение NOVA имеет неверную структуру.");

            var version = versionElement.GetInt32();
            if (version != CurrentVersion)
                throw new InvalidDataException($"Версия протокола NOVA {version} не поддерживается.");

            var typeName = typeElement.GetString();
            if (!TryParseType(typeName, out var type))
                throw new InvalidDataException("Тип управляющего сообщения NOVA неизвестен.");

            var transferId = transferIdElement.GetString();
            ValidateTransferId(transferId);
            var payload = payloadElement.Deserialize<T>(JsonOptions);
            if (payload is null) throw new InvalidDataException("В сообщении NOVA отсутствует полезная нагрузка.");

            return new TransferControlMessage<T>(version, type, transferId!, payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Управляющее сообщение NOVA содержит некорректный JSON.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException("Поля управляющего сообщения NOVA имеют неверные типы.", ex);
        }
    }

    public static byte[] EncodeDataBlock(Guid transferId, long offsetBytes, ReadOnlyMemory<byte> payload)
    {
        if (transferId == Guid.Empty) throw new ArgumentException("Идентификатор передачи не может быть пустым.", nameof(transferId));
        if (offsetBytes < 0 || offsetBytes > long.MaxValue - payload.Length)
            throw new ArgumentOutOfRangeException(nameof(offsetBytes));
        if (payload.Length is 0 or > MaximumDataBlockLength)
            throw new ArgumentOutOfRangeException(nameof(payload), $"Размер блока должен быть от 1 до {MaximumDataBlockLength} байт.");

        var frame = new byte[DataBlockHeaderLength + payload.Length];
        DataMagic.CopyTo(frame);
        frame[4] = CurrentVersion;
        frame[5] = 1; // file data block
        // Bytes 6 and 7 are reserved and must remain zero.
        if (!transferId.TryWriteBytes(frame.AsSpan(8, 16), bigEndian: true, out var idBytesWritten) || idBytesWritten != 16)
            throw new InvalidOperationException("Не удалось записать идентификатор передачи.");
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(24, 8), (ulong)offsetBytes);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(32, 4), (uint)payload.Length);
        payload.Span.CopyTo(frame.AsSpan(DataBlockHeaderLength));
        return frame;
    }

    public static TransferDataBlock DecodeDataBlock(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length < DataBlockHeaderLength + 1 || frame.Length > MaximumMessageLength)
            throw new InvalidDataException("Размер бинарного блока NOVA недопустим.");

        var bytes = frame.Span;
        if (!bytes[..4].SequenceEqual(DataMagic) || bytes[4] != CurrentVersion || bytes[5] != 1 || bytes[6] != 0 || bytes[7] != 0)
            throw new InvalidDataException("Заголовок бинарного блока NOVA некорректен или не поддерживается.");

        var transferId = new Guid(bytes.Slice(8, 16), bigEndian: true);
        var offset = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(24, 8));
        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(32, 4));
        if (transferId == Guid.Empty || offset > long.MaxValue || payloadLength == 0 ||
            payloadLength > MaximumDataBlockLength || payloadLength != frame.Length - DataBlockHeaderLength ||
            offset > (ulong)long.MaxValue - payloadLength)
            throw new InvalidDataException("Длина или смещение бинарного блока NOVA недопустимы.");

        return new TransferDataBlock(transferId, (long)offset, frame[DataBlockHeaderLength..]);
    }

    private static void EnsureNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("Сообщение NOVA содержит повторяющееся поле JSON.");
                EnsureNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) EnsureNoDuplicateProperties(item);
        }
    }

    private static void ValidateTransferId(string? transferId)
    {
        if (transferId is null || !Guid.TryParseExact(transferId, "N", out var parsed) ||
            !string.Equals(parsed.ToString("N"), transferId, StringComparison.Ordinal))
            throw new InvalidDataException("Идентификатор передачи должен быть каноническим GUID в формате N.");
    }

    private static bool IsKnownType(TransferControlType type) => type is
        TransferControlType.Offer or TransferControlType.Decision or TransferControlType.DeviceProof or
        TransferControlType.Resume or TransferControlType.BlockAcknowledgement or TransferControlType.Complete or
        TransferControlType.Failure or TransferControlType.Cancel;

    private static string TypeName(TransferControlType type) => type switch
    {
        TransferControlType.Offer => "offer",
        TransferControlType.Decision => "decision",
        TransferControlType.DeviceProof => "deviceProof",
        TransferControlType.Resume => "resume",
        TransferControlType.BlockAcknowledgement => "blockAck",
        TransferControlType.Complete => "complete",
        TransferControlType.Failure => "failure",
        TransferControlType.Cancel => "cancel",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static bool TryParseType(string? value, out TransferControlType type)
    {
        type = value switch
        {
            "offer" => TransferControlType.Offer,
            "decision" => TransferControlType.Decision,
            "deviceProof" => TransferControlType.DeviceProof,
            "resume" => TransferControlType.Resume,
            "blockAck" => TransferControlType.BlockAcknowledgement,
            "complete" => TransferControlType.Complete,
            "failure" => TransferControlType.Failure,
            "cancel" => TransferControlType.Cancel,
            _ => (TransferControlType)(-1)
        };
        return IsKnownType(type);
    }

    private sealed class FixedBufferWriter(int capacity) : IBufferWriter<byte>
    {
        private readonly byte[] _buffer = new byte[capacity];
        private int _written;

        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length - _written)
                throw new InvalidDataException("Управляющее сообщение NOVA превышает допустимый размер.");
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            _ = GetSpan(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
            if (sizeHint == 0) sizeHint = 1;
            if (sizeHint > _buffer.Length - _written)
                throw new InvalidDataException("Управляющее сообщение NOVA превышает допустимый размер.");
            return _buffer.AsSpan(_written);
        }

        public byte[] ToArray() => _buffer.AsSpan(0, _written).ToArray();
    }
}
