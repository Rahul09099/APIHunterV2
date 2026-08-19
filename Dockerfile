# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS publish
WORKDIR /src

# Copy everything (CLI folder is missing locally so it won't be copied)
COPY . .

# Publish the Web API project specifically
# This will automatically restore only the necessary dependencies
RUN dotnet publish UnsecuredAPIKeys.WebAPI/UnsecuredAPIKeys.WebAPI.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Copy published files
COPY --from=publish /app/publish .

# Create directory for database
RUN mkdir -p /app/data

# Set environment variables
ENV DATABASE_PATH=/app/data/unsecuredapikeys.db
ENV DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false
ENV DOTNET_USE_POLLING_FILE_WATCHER=true
ENV DOTNET_gcServer=0
ENV DOTNET_GCConserveMemory=5
ENV DOTNET_GCHeapHardLimitPercent=75

# Expose port (Render assigns this dynamically)
EXPOSE $PORT

ENTRYPOINT ["dotnet", "UnsecuredAPIKeys.WebAPI.dll"]
