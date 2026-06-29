FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/BookmarkManager.Api/BookmarkManager.Api.csproj src/BookmarkManager.Api/
COPY src/BookmarkManager.Client/BookmarkManager.Client.csproj src/BookmarkManager.Client/
COPY src/BookmarkManager.Contracts/BookmarkManager.Contracts.csproj src/BookmarkManager.Contracts/

RUN dotnet restore src/BookmarkManager.Api/BookmarkManager.Api.csproj

COPY src/ src/

RUN dotnet publish src/BookmarkManager.Api/BookmarkManager.Api.csproj \
    -c Release \
    -o /app/publish \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ConnectionStrings__Default=Data Source=/data/bookmarks.db

RUN rm -f /app/appsettings.Development.json

EXPOSE 8080
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "BookmarkManager.Api.dll"]
