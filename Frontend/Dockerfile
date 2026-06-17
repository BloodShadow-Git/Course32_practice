FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Frontend/Frontend.csproj", "Frontend/"]
RUN dotnet restore "Frontend/Frontend.csproj"
COPY . .
WORKDIR "/src/Frontend"
RUN dotnet publish -c Release -o /app/publish

FROM nginx:alpine
COPY Frontend/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
EXPOSE 80