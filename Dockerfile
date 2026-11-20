# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Domain/IntegrationHub.Domain.csproj Domain/
COPY Infrastructure/IntegrationHub.Infrastructure.csproj Infrastructure/
COPY IntegrationHub.csproj ./
RUN dotnet restore IntegrationHub.csproj
COPY . .
RUN dotnet publish IntegrationHub.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build --chown=app:app /app/publish .
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=true
EXPOSE 8080
ENTRYPOINT ["dotnet", "IntegrationHub.dll"]
