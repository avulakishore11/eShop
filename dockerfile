# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .

# 🔧 Only restore the specific project, not the full solution
RUN dotnet restore src/ApiGateways/Web.Bff.Yarp/Web.Bff.Yarp.csproj

# Publish the YARP gateway
WORKDIR /src/src/ApiGateways/Web.Bff.Yarp
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Web.Bff.Yarp.dll"]
