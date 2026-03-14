# .NET Raspberry Pi Zero Project Template

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

### Dependencies Used
- **Npgsql** (10.0.1) - PostgreSQL driver
- **Microsoft.Extensions.Configuration** (10.0.1) - Configuration base
- **Microsoft.Extensions.Configuration.Json** (10.0.1) - JSON config
- **Microsoft.Extensions.Configuration.EnvironmentVariables** (10.0.1) - Environment variables
- **Microsoft.Extensions.Configuration.CommandLine** (10.0.1) - CLI args
- **Microsoft.Extensions.Logging** (10.0.1) - Logging
- **Microsoft.Extensions.Logging.Console** (10.0.1) - Console logging
- **Polly** (8.6.5) - Resilience/retry policies

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
echo To Deploy: rsync -P build-output/AppName pi0.h.b:AppName
```

## Configuration Pattern

### appsettings.json
```json
{
  "ConnectionString": "",
  "GpsdHost": "localhost",
  "GpsdPort": 2947,
  "TcpReceiveTimeout": 15000,
  "TcpSendTimeout": 5000,
  "GpsdHandshakeTimeout": 5000,
  "GpsdReadTimeout": 15000,
  "InitialReconnectDelay": 1000,
  "MaxReconnectDelay": 60000,
  "DatabaseMaxRetries": 3,
  "DatabaseInitialRetryDelay": 100,
  "StatsReportingInterval": 60000,
  "ProgressReportInterval": 10,
  "ValidateMessages": true,
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
    .AddEnvironmentVariables(prefix: "GPSLOGGER_")
    .AddCommandLine(args, new Dictionary<string, string>
    {
        { "--connection-string", "ConnectionString" },
        { "--gpsd-host", "GpsdHost" },
        { "--gpsd-port", "GpsdPort" },
        { "--log-level", "Logging:LogLevel:Default" }
    });
```

### Config Class Pattern
```csharp
public class AppConfig
{
    public string ConnectionString { get; set; } = string.Empty;
    public string GpsdHost { get; set; } = "localhost";
    public int GpsdPort { get; set; } = 2947;
    public int TcpReceiveTimeout { get; set; } = 15000;
    public int TcpSendTimeout { get; set; } = 5000;
    public int GpsdHandshakeTimeout { get; set; } = 5000;
    public int GpsdReadTimeout { get; set; } = 15000;
    public int InitialReconnectDelay { get; set; } = 1000;
    public int MaxReconnectDelay { get; set; } = 60000;
    public int DatabaseMaxRetries { get; set; } = 3;
    public int DatabaseInitialRetryDelay { get; set; } = 100;
    public int StatsReportingInterval { get; set; } = 60000;
    public int ProgressReportInterval { get; set; } = 10;
    public bool ValidateMessages { get; set; } = true;
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
- Remote: `pi0.h.b:/home/dave/workspace/AppName.git`
- Deploy command: `rsync -P build-output/AppName pi0.h.b:AppName`

## Invariant Globalization
Set `<InvariantGlobalization>true</InvariantGlobalization>` in .csproj to avoid loading ICU data (~10MB savings on ARM64).
