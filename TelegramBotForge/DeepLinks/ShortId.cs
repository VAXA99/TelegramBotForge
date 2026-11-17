namespace TelegramBotForge.DeepLinks;

/// <summary>Canonical 22-character base64url GUIDs using .NET Guid byte order, for links and callback data.</summary>
public static class ShortId
{
    public static string Encode(Guid value) => Convert.ToBase64String(value.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static Guid Decode(string value) => TryDecode(value, out var result)
        ? result : throw new FormatException("A short GUID must be a canonical 22-character base64url value.");

    public static bool TryDecode(string? value, out Guid result)
    {
        result = default;
        if (value is not { Length: 22 } || !value.All(DeepLinkPayload.IsCharacter))
            return false;
        Span<byte> bytes = stackalloc byte[16];
        if (!Convert.TryFromBase64String(value.Replace('-', '+').Replace('_', '/') + "==", bytes, out var written) || written != 16)
            return false;
        var decoded = new Guid(bytes);
        if (Encode(decoded) != value)
            return false;
        result = decoded;
        return true;
    }
}
