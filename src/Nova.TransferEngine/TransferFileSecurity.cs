namespace Nova.TransferEngine;

public static class TransferFileSecurity
{
    public static string ValidateAndResolve(string destinationDirectory, string incomingName)
    {
        if (string.IsNullOrWhiteSpace(incomingName) || incomingName.Length > 255)
            throw new InvalidDataException("Имя файла пустое или слишком длинное.");
        if (incomingName.Contains('/') || incomingName.Contains('\\') || Path.IsPathRooted(incomingName))
            throw new InvalidDataException("Путь передачи содержит запрещённые компоненты.");

        var invalid = Path.GetInvalidFileNameChars();
        if (incomingName.Any(c => c < 32 || invalid.Contains(c)) || incomingName is "." or ".." || incomingName.EndsWith(' ') || incomingName.EndsWith('.'))
            throw new InvalidDataException("Имя файла содержит недопустимые символы.");

        var root = Path.GetFullPath(destinationDirectory);
        var candidate = Path.GetFullPath(Path.Combine(root, incomingName));
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Файл выходит за пределы папки назначения.");
        return candidate;
    }

    public static string MakeUniquePath(string destinationDirectory, string incomingName)
    {
        var original = ValidateAndResolve(destinationDirectory, incomingName);
        if (!File.Exists(original)) return original;
        var stem = Path.GetFileNameWithoutExtension(incomingName);
        var extension = Path.GetExtension(incomingName);
        for (var index = 2; index < int.MaxValue; index++)
        {
            var name = $"{stem} ({index}){extension}";
            var candidate = ValidateAndResolve(destinationDirectory, name);
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("Не удалось подобрать свободное имя файла.");
    }
}
