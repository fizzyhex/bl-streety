# Optional BONELAB MCP adapter

Debug builds include `streety_warp` and `streety_status` by default when the helper repository is found; opt out with `-p:EnableBonelabMcpAdapter=false`, or force it on for other configurations with `-p:EnableBonelabMcpAdapter=true`. If the helper is missing, debug builds warn and build without the adapter. Release builds exclude the adapter and have no reference to `BonelabMcp.Contracts`.

The default helper location is the sibling `bonelab-mcp` repository; override `BonelabMcpRoot` when needed. Build with `-p:DeployToGame=false` for checks that must not replace the installed Streety DLL. Install a development build explicitly with BONELAB closed, alongside the helper bridge and its contracts in `UserLibs`.

Registration happens on the main thread only when the contracts assembly is loaded and the helper is enabled. Warp starts the existing Streety session operation and returns acceptance. Status reports session availability, status text, location name and `isWarping`. Poll until idle, then check the final status to distinguish success from failure.

See the helper's [README](../../../../bonelab-mcp/README.md) for MCP configuration, CLI usage, deployment and live tests. The Streety level still needs a player marker and a rebuilt/copied scene bundle so the local rig can spawn.
