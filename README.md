
# Relic Launcher for revisiting the Golden Apple Archipelago from a certain anime game

Welcome, Travelers from afar, protoshifters, and protobuffers. This anime game launcher, which might become your best friend, is made specifically for versions 1.6 & 2.8 anime game events and will make managing your own server much easier. I bet the last two categories won't have any use for this program, unless they want to see the complicated code regarding the [enhancements](#game-enhancements), the 1.6/2.8 server event fixes, or the creepy-pasta code that multiplies user accounts. With that said, let's look at the [features](#features).

  

# Features

-  **Easily install the game versions for you**, apply all the needed patches, and install & configure Fiddler for you [CORE FUNCTION]

-  **Ability to auto-isolate profiles from official servers and private ones**. This way, you don't have to re-login every time you change the game version [CORE FUNCTION]

- Ability to **automatically**  ***apply server-side fixes*** for **1.6 and 2.8 events** [CORE FUNCTION]

-  **Control both 1.6 and 2.8 servers** through the Linux/Windows agent, just like a game control panel [CORE FUNCTION]

-  **Game events [1.6/2.8] / Spiral Abyss** (if anyone wants to toy with it) are **automatically extended** [CORE FUNCTION]

-  **Easily port-forward servers that are behind a NAT network** [easily configured in the agent installation script]

-  **Ability to copy one's user game data to another account**. This way, you can either play both the 1.6 & 2.8 events in co-op, explore the map, or give another player the ability to replay the older story quests—the full experience, in other words. There is also default game progress to be copied (before and after the GAA quest, for exploration purposes or playing mini-games like Kaboomball Kombat together in co-op).

-  **Game Enhancements** enriched from upstream versions, adapted to both **1.6 & 2.8 versions**. All the **spakebi** features are ported back down to the 1.6 version. Some features may be broken. The main upstream was 3.3.

- Apply client-side hotpatches from the server (ability to turn it off/on) - external downloader required

  
# Launcher Requirments
Please make sure you install [vc_redist.x86](https://aka.ms/vs/17/release/vc_redist.x64.exe)(unnecessary now with the static build of injector).
# Launcher Description

The Launcher has 2 modes: **Player Mode** & **Server Admin Mode** (which requires a server-side token).

  

The **Player Mode** lets you choose an IP for the server (which could be the server's DNS or public IP) and prepares the game to be ready to play on the servers (it prepares everything for you: from the DLL patch for mhynot2 to the Fiddler installation with the auto-prepared script made for your server's IP). You just have to press Launch (after you install or choose the game path for the specific version the server is on)! It also lets you create an account with progress both before and after the GAA quest, **if the server administrator permits it** (if the server owner is using the launcher's agent on their server).

It also lets you use the Game GM Commands easily, featuring an intuitive UI (for those servers using the launcher's server agent).

  

The **Server Admin Mode** lets you have all the features described in **Player Mode**, plus full server controls. It also has the ability to easily install the **server agent** on your Windows or Linux machine. Supported environments for the agent include Debian-based (e.g., Ubuntu) and RHEL-based (e.g., CentOS, AlmaLinux) distributions, as well as Windows.

  

***

  

## The server controls are as follows:

  

- Start/Restart the server
- Setup IP addreses/Help reach behind NAT

- Allow players to create new accounts from pre-baked game progress (after and before GAA Quest)

- Copy one player's progress to a new account

- Allow players to use in-game GM commands

- Toggle hotpatch application for the game client
- Multiple Languages download like on the official server
- Gameplay tweaks: edit spiral abyss floors line-up, quest reward data, resin, domain drops
 

## Server requirements for running on a Windows machine

Please refer to the integrated PDF guide. Be sure to install Docker and WSL.

  

    powershell:
    
	    wsl.exe --set-default-version 2
	    
	    wsl --install
	    
	    winget install -e --id Python.Python.3.12
For windows 10, you will need a third party archive extractor, like [7-zip](https://www.7-zip.org/).

	    winget install --id 7zip.7zip -e

**!** Never run the server setup while Fiddler is running. Close it and run it afterward.

  

***

  

## Game Enhancements

Yep, you know what it is, and its 3.3 spakebi features are ported downwards to 1.6, alongside some minor new features. Some things may be broken, though. Hope you'll have a great experience, and thanks to all the developers who made this possible. Love <3

  

## Agent installation (Windows/Linux)

Some simple knowledge I want you to have before you begin: **The Advertised IP** means your **external IP**. So if you're behind a NAT network, for example, and want people to connect to your server, please remember to put your **external** IPv4 address there. It's for the dispatcher server, which tells the client where to connect (like the region, for example).

You can simply install the agent on both operating systems directly from **Admin Mode** -> Install it on a new server. For **Windows**, you must install WSL2 + Docker **BEFORE** installation.

  

**Ports to open**:


| Port | Protocol | Service | Role | Required |
|------|----------|---------|------|----------|
| **21000** | **TCP** | sdk | login + query_region (dispatch) | ✅ MANDATORY |
| **21081** | **UDP** | gateserver | game traffic | ✅ MANDATORY |
| 21051 | TCP | muipserver | GM/MUIP | optional |
| 8085 | TCP | adminer | admin DB web | optional |
| 8087 | TCP | phpMyAdmin | admin DB web | optional |

  

### **For manual installation in Linux:**

1. Create a directory for the desired servers (1.6 or 2.8):

`mkdir -p /home/relic/1.6_live`

`mkdir -p /home/relic/2.8_live`

2. Prepare the docker stack (including the game servers, etc.; follow the integrated gio-guide for reference).

3. Drag & drop the agent folder from this repo, `chmod 755 install_agent.sh`, `sudo ./install_agent.sh`, and follow the installation steps.

4. Save the agent token; you'll have to use it in your launcher. The token is located at `sudo cat /etc/gio-agent/config`.

  

# Build

Build it using Windows.

Requirements: .NET 10 SDK, Python 3, Inno Setup 6. For the .dll patch, you also need `VS Build Tools 2026` with `MSVC v145` and `Windows SDK`.

  

    dotnet run --project app/Relic.App # run the app
    
    dotnet run --project spikes/Relic.Spikes -- all # the mechanics test suite
    
    python build/check_i18n.py # validate translations, regenerate ui/lang/en.js
    
    .\build\publish.ps1 ` # self-contained single-file win-x64 build
    
    --default-server=game.example.com ` # (optional) one default server
    
    --default-servers="EU=eu.example.com;LAN=192.0.2.10" `# (optional) several, offered as quick picks
    
    --default-mode=player ` # (optional) player | admin
    
    --theme=summer ` # (optional) default theme summer | classic
    
    # admin builds only: --token=<agent token> (default: contents of config\agent.token if present)
    
    & "C:\Program Files (x86)\Inno Setup 6\iscc.exe" installer\relic.iss # -> installer\Output\RelicSetup.exe

  

## Credits

- Biosnod, Hotaru, the community - for server stacks (Docker files, guided PDF, and all their work towards gio)

-  **mhynot2** by khang06 — https://github.com/khang06/mhynot2. The shipped `payload/common/ayy/anime/build/launcher.exe`

-  **Akebi GC** — [Taiga](https://github.com/Taiga74164)

- The optional **in-game enhancements menu (F1)** is a modified backport to game 1.6/2.8

-  **2.8 `global-metadata.dat` patch** — the community

-  **GAA server-side data fixes** (the `NewActivityCondData.txt` family) — thanks to the amazing work of @AZ#7011, amspy, mhypbase.dll, and the community effort.

-  **Dimbreath/AnimeGameData** (game data used by `build/make_catalog.py`), **GrasscutterCommandGenerator** (GM command reference).

## Feeling generous?
If you feel that my work is worth anything, and want to support future development/projects, you can buy me a [ko-fi]() ^^. Ain't exactly living in a luxury, worst than that, at this current state I'm a self-employed employee of tha year
Thank you so much for any small amount of support!