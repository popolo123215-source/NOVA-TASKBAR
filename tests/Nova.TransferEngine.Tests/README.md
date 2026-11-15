# Локальные проверки Transfer Engine

В проекте нет стороннего test framework. Запуск консольного набора:

```powershell
dotnet run --project tests/Nova.TransferEngine.Tests/Nova.TransferEngine.Tests.csproj -c Debug
```

Набор проверяет framing LAN-протокола, частичные чтения, недопустимые размеры сообщений, traversal-защиту, объявление устройства, взаимный TLS, согласие получателя, потоковую передачу 12 MiB, SHA-256, сохранение истории и wire-кодек Internet Transfer Protocol (управляющие сообщения, ограничения размера, версии, формат блоков и проверку повреждённых кадров).
