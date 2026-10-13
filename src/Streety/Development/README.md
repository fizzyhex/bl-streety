# BONELAB MCP Adapter

streety supports an optional MCP adapter that exposes warp and session status functionality through the `bonelab-mcp` helper. intended for development, but may have other uses too!

## Build Configuration

The adapter is enabled by default in **Debug builds** when the helper repository is available.

| Configuration | Behaviour |
|---|---|
| Debug | Enables the adapter if the helper is found |
| Release | Excludes the adapter and its `BonelabMcp.Contracts` reference |
| Helper missing | Emits a warning and builds without the adapter |

You can override the default behaviour using MSBuild properties:

```bash
# Disable the adapter
-p:EnableBonelabMcpAdapter=false

# Explicitly enable the adapter
-p:EnableBonelabMcpAdapter=true
```

By default, the build looks for `bonelab-mcp` in a sibling directory. Set `BonelabMcpRoot` to use a different location.

## Building and Deployment

To build without replacing the installed Streety DLL:

```bash
-p:DeployToGame=false
```

For development deployment:

1. Close BONELAB.
2. Build Streety with the MCP adapter enabled.
3. Install the development build.
4. Ensure the helper bridge and its contracts are installed in `UserLibs`.

## Available MCP Tools

The adapter exposes two tools:

| Tool | Description |
|---|---|
| `streety_warp` | Starts Streety's existing session operation and returns whether the request was accepted |
| `streety_status` | Reports session availability, status text, location name, and `isWarping` |

**Warp operations are asynchronous.** An accepted request does not necessarily mean the warp succeeded.

After calling `streety_warp`, poll `streety_status` until `isWarping` is false, then inspect the final status to determine whether the operation succeeded or failed.

### Registration

Tool registration occurs on the main thread and only when both conditions are met:

- The contracts assembly is loaded.
- The MCP helper is enabled.

## Known Requirements

The Streety level still requires a **player marker** and a rebuilt/copied scene bundle for the local rig to spawn correctly.

## Further Documentation

See the [BONELAB MCP helper README](https://github.com/fizzyhex/bonelab-mcp/README.md) for MCP configuration, CLI usage, deployment instructions, and live testing.
