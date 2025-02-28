FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

ADD . /S63Tools

WORKDIR /S63Tools/S63Tools

RUN dotnet publish -c Release -o /out


FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime

WORKDIR /app

COPY --from=build /out .

ENTRYPOINT ["dotnet", "S63Tools.dll"]
