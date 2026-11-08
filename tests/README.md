## Build and smoke checks

The smoke executable sends messages only to its own hidden windows. It never
injects input, displays UI, or changes settings. Build into a unique directory:

```powershell
$artifacts = Join-Path $PWD ('.artifacts/check-' + [guid]::NewGuid().ToString('N'))
dotnet build tests/ReceiveCopyData.Smoke/ReceiveCopyData.Smoke.csproj -c Release -p:PlatformTarget=x64 --artifacts-path $artifacts -warnaserror
dotnet "$artifacts/bin/ReceiveCopyData.Smoke/release/ReceiveCopyData.Smoke.dll"
```

For x86, use a fresh directory, `-p:PlatformTarget=x86`, and the x86 dotnet host.
For NativeAOT, publish with `-r win-x64` or `-r win-x86`,
`-p:PublishAot=true`, and a matching PlatformTarget; run the published executable
with `--require-aot`. Signing and execution may be coordinated by the caller.
