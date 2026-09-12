# netfox.cs.steam

A Godot `MultiplayerPeer` over Steam's relay sockets, written in C#. Listen-server: the host is peer 1 and relays
between clients, the same shape netfox expects from ENet.

```csharp
// Host
var transport = SteamNetworkingTransport.Host();
Multiplayer.MultiplayerPeer = SteamMultiplayerPeer.Host(transport);

// Client
var transport = SteamNetworkingTransport.Join(hostSteamId);
Multiplayer.MultiplayerPeer = SteamMultiplayerPeer.Client(transport);
```

`SteamClient.Init` and the lobby are the caller's business - this addon only carries packets. netfox never creates
peers itself, so once the peer is assigned everything behaves exactly as it does over ENet.

## Enabling it

Off by default, because the Facepunch package is Windows-only:

```xml
<PropertyGroup>
  <DefineConstants>$(DefineConstants);NETFOX_STEAM</DefineConstants>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Facepunch.Steamworks" Version="2.3.3" />
</ItemGroup>
```

In this repository: `dotnet build Netfox.csproj -p:NetfoxSteam=true`.

Without the define, `SteamMultiplayerPeer` still compiles and is still covered - it works against any
`ISteamTransport`, and the tests use a fake one.

## Platforms

| Platform | Assembly | Native library |
|---|---|---|
| Windows x64 | `Facepunch.Steamworks` on NuGet (this is the Win64 build) | `steam_api64.dll`, bundled |
| Windows x86 | `Facepunch.Steamworks.Win32` on NuGet | `steam_api.dll`, bundled |
| Linux, macOS | `Facepunch.Steamworks.Posix.csproj`, **built from source** | `libsteam_api.so` / `.dylib` from the Steamworks SDK |

There is no Posix package on NuGet, and the Windows assembly cannot stand in for it. `Utility/Platform.cs` sets
`StructPlatformPackSize` to 8 on Windows and 4 on Posix, so the managed marshalling layout differs - pointing the
Win64 assembly at a different native library is not enough. Reference the platform's assembly at build time.

This is why every Steamworks call lives in `SteamNetworkingTransport.cs`: the platform choice is confined to one file.

## What is verified

The peer's logic - id handshake, relaying, broadcast with exclusion, transfer modes and channels, disconnects in both
directions, refusing connections - is covered by `test/Steam/SteamPeerTests.cs` against a fake transport, and runs in
CI. The header costs 3 bytes per packet for a session of this size.

`SteamNetworkingTransport` compiles against Facepunch 2.3.3 but has **not** been run against a live Steam client.
That needs a Steam account, and two of them for a real session - tracked in #6.
