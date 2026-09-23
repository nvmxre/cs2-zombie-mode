# Sound addon

Zombie mode without sound is half a mode. The core plugin plays sound **events** by name (`zombiemode.*`,
configurable in the `sounds` section of `ZombieMode.json`); the sound files themselves reach players in a
Workshop addon.

## Use the published addon

1. Install [MultiAddonManager](https://github.com/Source2ZE/MultiAddonManager) on your server.
2. Add the addon's Workshop ID to `mm_extra_addons` in `csgo/cfg/multiaddonmanager/multiaddonmanager.cfg`.
3. Players download it automatically when they join.

## Build it yourself

1. `python sounds/gen_soundevents.py` — writes `soundevents/soundevents_addon.vsndevts`.
2. `powershell -File sounds/build.ps1` — compiles the addon with the CS2 Workshop Tools.
3. Publish `game/csgo_addons/cs2_zombie_mode_sounds` from the Workshop Tools and put your own ID in
   `mm_extra_addons`.

To replace a sound, drop a file with the same name into `files/` and rebuild. To add a new pool (say, eight growls
instead of six), add files, extend the loop in `gen_soundevents.py`, and raise `zombieIdleCount` in the config.

Where each file comes from: [SOURCES.md](SOURCES.md).
