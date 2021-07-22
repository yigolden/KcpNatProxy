dotnet publish src\KcpNatProxy.Server\KcpNatProxy.Server.csproj -c Release -r win-x64 -o artifacts\win-x64 --self-contained=true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:PublishReadyToRun=true
dotnet publish src\KcpNatProxy.Server\KcpNatProxy.Server.csproj -c Release -r linux-x64 -o artifacts\linux-x64 --self-contained=true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:PublishReadyToRun=true
dotnet publish src\KcpNatProxy.Server\KcpNatProxy.Server.csproj -c Release -r win-arm64 -o artifacts\win-arm64 --self-contained=true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:PublishReadyToRun=true
dotnet publish src\KcpNatProxy.Server\KcpNatProxy.Server.csproj -c Release -r linux-arm64 -o artifacts\linux-arm64 --self-contained=true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:PublishReadyToRun=true

dotnet publish src\KcpNatProxy.Client\KcpNatProxy.Client.csproj -c Release -r win-x64 -o artifacts\win-x64 --self-contained=true -p:PublishSingleFile=true -p:PublishTrimmed=true
dotnet publish src\KcpNatProxy.Client\KcpNatProxy.Client.csproj -c Release -r linux-x64 -o artifacts\linux-x64 --self-contained=true -p:PublishSingleFile=true -p:PublishTrimmed=true
dotnet publish src\KcpNatProxy.Client\KcpNatProxy.Client.csproj -c Release -r win-arm64 -o artifacts\win-arm64 --self-contained=true -p:PublishSingleFile=true -p:PublishTrimmed=true
dotnet publish src\KcpNatProxy.Client\KcpNatProxy.Client.csproj -c Release -r linux-arm64 -o artifacts\linux-arm64 --self-contained=true -p:PublishSingleFile=true -p:PublishTrimmed=true
