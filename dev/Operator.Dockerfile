FROM mcr.microsoft.com/dotnet/aspnet:10.0.12@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4
WORKDIR /app
COPY --chown=$APP_UID:$APP_UID .build/operator/ .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "SupervisorOperator.dll"]
