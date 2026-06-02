# .NET Raspberry Pi Zero Project Template

> **Origin / purpose:** These notes come from a separate .NET-on-Raspberry-Pi project, not from CarPi itself. They are kept here only as a **generic reference** for cross-compiling and deploying AOT .NET apps to a Pi Zero (ARM64). Treat `AppName` and the config examples as placeholders — they do not describe CarPi's actual configuration.

## Overview
This document captures the setup and patterns used for .NET projects targeting Raspberry Pi (ARM64).

## Project Configuration

### Target Framework
- .NET 10.0 (AOT-compiled for ARM64)

### Key .csproj Settings
```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

### Common Dependencies
Pick what the app needs; these are the generic building blocks used across Pi projects:
- **Microsoft.Extensions.Configuration** (10.0.1) - Configuration base
- **Microsoft.Extensions.Configuration.Json** (10.0.1) - JSON config
- **Microsoft.Extensions.Configuration.EnvironmentVariables** (10.0.1) - Environment variables
- **Microsoft.Extensions.Configuration.CommandLine** (10.0.1) - CLI args
- **Microsoft.Extensions.Logging** (10.0.1) - Logging
- **Microsoft.Extensions.Logging.Console** (10.0.1) - Console logging

## Cross-Compilation with Docker

### Dockerfile Pattern
```dockerfile
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS builder

# Configure multiarch for cross-compilation to ARM64
RUN dpkg --add-architecture arm64 && \
    sed -i 's|^Types: deb|Types: deb\nArchitectures: amd64|' /etc/apt/sources.list.d/ubuntu.sources && \
    cat <<EOF >> /etc/apt/sources.list.d/arm64.sources
Types: deb
URIs: http://ports.ubuntu.com/ubuntu-ports
Suites: noble noble-updates noble-security
Components: main universe
Architectures: arm64
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg
EOF

RUN apt-get update && apt-get install -y \
    clang \
    llvm \
    binutils-aarch64-linux-gnu \
    gcc-aarch64-linux-gnu \
    libc6-dev:arm64 \
    zlib1g-dev:arm64

WORKDIR /app
COPY . .
RUN dotnet publish -c Release -o out -a arm64 -p:PublishAot=true --self-contained

# Export stage - copies output to a volume for extraction
FROM scratch AS export
COPY --from=builder /app/out /

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
WORKDIR /app
COPY --from=builder /app/out .
ENTRYPOINT ["dotnet", "AppName.dll"]
```

### Build Script
```bash
#!/bin/bash
set -e
docker build --target export --output ./build-output .
echo To Deploy: rsync -P build-output/AppName <pi-host>:AppName
```

## Configuration Pattern

### appsettings.json
```json
{
  "ExampleSetting": "value",
  "ExampleTimeout": 15000,
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "AppName": "Information",
      "Microsoft": "Warning"
    }
  }
}
```

### Configuration Loading
- JSON file: `appsettings.json` in output directory
- Environment variables: Prefix with `APPNAME_`
- Command line arguments: Support --key value format

```csharp
var configBuilder = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "APPNAME_")
    .AddCommandLine(args, new Dictionary<string, string>
    {
        { "--example-setting", "ExampleSetting" },
        { "--log-level", "Logging:LogLevel:Default" }
    });
```

### Config Class Pattern
```csharp
public class AppConfig
{
    public string ExampleSetting { get; set; } = string.Empty;
    public int ExampleTimeout { get; set; } = 15000;
}
```

## Logging Pattern
```csharp
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddConfiguration(configuration.GetSection("Logging"))
        .AddSimpleConsole(options =>
        {
            options.IncludeScopes = false;
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });
});

var logger = loggerFactory.CreateLogger<Program>();
```

## Graceful Shutdown Pattern
```csharp
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (sender, e) =>
{
    logger.LogInformation("Shutdown signal received (Ctrl+C)");
    e.Cancel = true;
    cts.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
{
    logger.LogInformation("Process exit signal received");
    cts.Cancel();
};

await processor.RunAsync(cts.Token);
```

## Deployment
- Remote: `<pi-host>:/home/<user>/workspace/AppName.git`
- Deploy command: `rsync -P build-output/AppName <pi-host>:AppName`

## Invariant Globalization
Set `<InvariantGlobalization>true</InvariantGlobalization>` in .csproj to avoid loading ICU data (~10MB savings on ARM64).
