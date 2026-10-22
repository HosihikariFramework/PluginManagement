#!/usr/bin/env bash
# Pack the Build package, then use its PackPlugin target on a throwaway plugin.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

dotnet pack "$here/Hosihikari.Plugin.Build.csproj" -c Release -o "$work/packages" --nologo

plugin="$work/TestPlugin"
mkdir -p "$plugin"

cat > "$plugin/TestPlugin.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>TestPlugin</AssemblyName>
    <RootNamespace>TestPlugin</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Hosihikari.Plugin.Abstractions" Version="[1.0.0]" ExcludeAssets="runtime" />
    <PackageReference Include="Hosihikari.Plugin.Build" Version="1.0.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
EOF

cat > "$plugin/Plugin.cs" <<'EOF'
using Hosihikari;

[assembly: EntryPoint<TestPlugin.Plugin>]

namespace TestPlugin;

public sealed class Plugin : IPlugin
{
    public void OnLoad(IPluginContext context) { }
    public PluginHookResult OnEnable() => PluginHookResult.Allow();
    public PluginHookResult OnDisable() => PluginHookResult.Allow();
    public void OnUnload() { }
}
EOF

cat > "$plugin/manifest.json" <<'EOF'
{
  "name": "TestPlugin",
  "entry": "TestPlugin.dll",
  "type": "hosihikari",
  "version": "1.0.0",
  "extraInfo": {
    "api_version": "2",
    "manifest_version": "1",
    "loader": "assembly"
  }
}
EOF

dotnet build "$plugin/TestPlugin.csproj" -t:PackPlugin -c Release --nologo \
  -p:RestoreSources="$work/packages;https://api.nuget.org/v3/index.json" \
  -p:HosihikariPluginOutputRoot="$plugin/dist"

test -f "$plugin/dist/TestPlugin/TestPlugin.dll"
test -f "$plugin/dist/TestPlugin/manifest.json"
if find "$plugin/dist/TestPlugin" -name 'Hosihikari.Plugin.Abstractions.dll' | grep -q .; then
  echo 'shared contract leaked into the plugin package' >&2
  exit 1
fi

echo 'PackPlugin smoke passed'
