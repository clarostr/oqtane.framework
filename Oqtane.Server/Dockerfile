#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER app
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:5000

EXPOSE 5000

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Oqtane.Server/Oqtane.Server.csproj", "Oqtane.Server/"]
COPY ["Oqtane.Client/Oqtane.Client.csproj", "Oqtane.Client/"]
COPY ["Oqtane.Shared/Oqtane.Shared.csproj", "Oqtane.Shared/"]
RUN dotnet restore "./Oqtane.Server/./Oqtane.Server.csproj"
COPY . .
WORKDIR "/src/Oqtane.Server"
RUN dotnet build "./Oqtane.Server.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Oqtane.Server.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Oqtane.Server.dll"]
