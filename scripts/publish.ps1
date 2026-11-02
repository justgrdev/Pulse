# Builds a ReadyToRun copy of Pulse into .\dist
$root = Split-Path $PSScriptRoot -Parent
dotnet publish "$root\Pulse.csproj" -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o "$root\dist"
