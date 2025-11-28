# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Copy project files (including referenced projects) first for restore layer caching
COPY ["AVMTradeReporter.Models/AVMTradeReporter.Models.csproj", "AVMTradeReporter.Models/"]
COPY ["AVMTradeReporter.Subscriber/AVMTradeReporter.Subscriber.csproj", "AVMTradeReporter.Subscriber/"]
RUN dotnet restore "./AVMTradeReporter.Subscriber/AVMTradeReporter.Subscriber.csproj"
# Copy the remaining source
COPY . .
WORKDIR "/src/AVMTradeReporter.Subscriber"
RUN dotnet build "./AVMTradeReporter.Subscriber.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./AVMTradeReporter.Subscriber.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AVMTradeReporter.Subscriber.dll"]