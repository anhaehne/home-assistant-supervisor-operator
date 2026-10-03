FROM mcr.microsoft.com/dotnet/aspnet:10.0.12@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4
WORKDIR /app
COPY --chown=0:0 .build/gateway/ .
# Stock Core owns its privileged Unix socket as root:root, mode 0600.
USER 0
ENV ASPNETCORE_HTTP_PORTS=8081
ENTRYPOINT ["dotnet", "CoreGateway.dll"]
