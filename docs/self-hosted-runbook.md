# Run BEefy

BEefy is started with `dotnet run`, not Docker.

From the BEefy repo:

```bash
env \
  OpenJibo__State__Backend=Sqlite \
  OpenJibo__State__PersistencePath=/var/lib/5x1/cloud-state.json \
  OpenJibo__Stt__EnableStreamingSherpa=true \
  CONTAINER_APP_REVISION=5x1 \
  OpenJibo__Deployment__Mode=managed \
  OpenJibo__PersonalMemory__Backend=Sqlite \
  OpenJibo__PersonalMemory__PersistencePath=/var/lib/5x1/personal-memory.json \
  ASPNETCORE_URLS=http://0.0.0.0:24667 \
  dotnet run \
  --project src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Api/Jibo.Cloud.Api.csproj \
  --no-launch-profile
```

Speech uses Sherpa (`OpenJibo__Stt__EnableStreamingSherpa=true`). State stays in the two Sqlite files under `/var/lib/5x1`. Robots reach this process through `api.5x1.com:443`. OTA stays on BEaker at `http://joap.5x1.com:80`.

Hey Jibo runs entirely in .NET with embedded grammar and dialog resources. No Node installation or helper services are required. See [native command coverage](native-conversation.md).
