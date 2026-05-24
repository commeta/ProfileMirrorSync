# Contributing

Thanks for your interest in improving ProfileMirrorSync.

## Building

```bat
dotnet build src/ProfileMirrorSync.csproj -c Release
dotnet test  tests/ProfileMirrorSync.Tests.csproj -c Release
```

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).
The project targets `net9.0-windows` (WinForms), so it builds and tests on
Windows.

## Guidelines

- Keep changes small and focused; each pull request should address one thing.
- Add or update tests for behavioural changes, and make sure `dotnet test`
  passes before submitting.
- Match the existing code style and comment conventions.
- The application UI is in Russian by design; please keep user-facing strings
  consistent with the existing language. Code comments are in English.

## Reporting issues

Please include your Windows version, the app version, and the relevant lines
from the log (`%LocalAppData%\ProfileMirrorSync\Logs`). Redact any sensitive
paths or
share names.
