# Wiring CS1 Mayor into opencode

## 1. Prerequisites

- The game is running with the **Skylines Agent Bridge** mod enabled and a city loaded.
- The MCP server is built:
  ```powershell
  dotnet build mcp-server\Cs1Mayor.Mcp.csproj -c Release
  ```

## 2. Configuration

opencode reads MCP servers from `mcp.servers.<name>`.

**Global** — `C:\Users\<you>\.config\opencode\opencode.json`
**Per project** — `<project>\opencode.json` or `<project>\.opencode\opencode.json`

Add:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "cs1-mayor": {
        "type": "local",
        "command": [
          "dotnet",
          "C:\\Users\\chichi\\Desktop\\projects\\cs1-mayor\\mcp-server\\bin\\Release\\net10.0\\Cs1Mayor.Mcp.dll"
        ],
        "environment": {
          "CS1_BRIDGE_URL": "http://127.0.0.1:32123"
        },
        "codemode": true
      }
    }
  }
}
```

### Schema notes (verified against opencode v2.2.6)

| Field | Notes |
|---|---|
| `type` | `"local"` or `"remote"`. **`remote` is Streamable HTTP only** — there is no SSE transport. |
| `command` | **Array**, executable then args. No shell, so spaces in paths need no quoting. |
| `environment` | Merged into the host environment. Supports `{env:NAME}` substitution. |
| `codemode` | Defaults to `true`. Exposes tools through Code Mode instead of the provider's native tool list. **Recommended here** — it keeps tool schemas out of the native tool list and gives the agent a searchable namespace. |
| `disabled` | `true` = configured but not connected. (Note: `enabled` is the deprecated V1 shape.) |
| `timeout` | `{startup, catalog, execution}` in ms. Defaults: 30 000 / 30 000 / 12 h. |

`mcp.servers.<name>` is the current shape. The older `mcp.<name>` form is deprecated.

> **Your existing config already uses the correct V2 shape** for `playwright`,
> `chrome-devtools`, `cua-driver` and `firecrawl` — just add `cs1-mayor` alongside them.

## 3. Verify

```powershell
opencode mcp list
```

Expected:

```
✓ cs1-mayor  connected
```

> ⚠️ **On this machine the CLI needs `XDG_CONFIG_HOME` set**, otherwise it reports
> "No MCP servers configured" even though the running session has loaded them fine:
>
> ```powershell
> $env:XDG_CONFIG_HOME = "$env:USERPROFILE\.config"
> opencode mcp list
> ```
>
> The **running opencode session** resolves the global config correctly either way — the
> tool catalog shows `cs1-mayor (13 tools)` regardless. The CLI quirk is cosmetic, but it
> will confuse you while debugging.

In an opencode session, `/mcps` opens the MCP dialog.

### If it fails to connect

| Symptom | Fix |
|---|---|
| `failed: spawn dotnet ENOENT` | Use the absolute path to `dotnet.exe`, not `dotnet`. |
| No response / timeout | The MCP server logs to **stderr**. Check `CS1_BRIDGE_URL` — it must point at the running game. |
| `Unknown tool: 'CitySnapshot'` | The C# SDK **lower-cases tool names**. Always call the exact name from `tools/list` (which is already snake_case here). |
| Tools list is empty | The game must be running with the mod enabled and a city loaded. |

### Quick protocol smoke test

```powershell
'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}' |
  dotnet .\mcp-server\bin\Release\net10.0\Cs1Mayor.Mcp.dll
```

You should get exactly one line of JSON back.

Or use the full test:

```powershell
python scripts\smoke-test-mcp.py
```

---

## 4. How the agent sees the tools

With `codemode: true`, opencode exposes the tools as a namespace in Code Mode:

```
- cs1-mayor (13 tools)
  - tools.cs1_mayor.city_snapshot(query: ...) // Read a compact summary of the whole city...
```

- The inline listing has a **2 000-token budget** and shows only the **first line** of each
  description, truncated to **120 characters**. Keep descriptions short and keyword-dense.
- Tools not in the inline listing are found with the global `search(...)` function, which
  returns the full description on demand.
- Tool names are normalised to `<server>_<tool>` with anything outside `[A-Za-z0-9_-]`
  replaced by `_`.

The server also sends `instructions` in the `initialize` result, which opencode injects into
the agent's system prompt. That is where the "you are the mayor" framing and the core
operating rules live — see `Program.cs`.

---

## 5. Optional: publish the knowledge base as MCP resources

The knowledge base in `docs/` is large (~2,000 lines of dense reference). Serving it as MCP
**resources** rather than tools means it costs **zero context** until the agent actually
reads a page.

```csharp
// In Program.cs, alongside .WithTools<CityTools>()
builder.Services
    .AddMcpServer(...)
    .WithStdioServerTransport()
    .WithTools<CityTools>()
    .WithResources<KnowledgeResources>();
```

with:

```csharp
[McpServerResourceType]
public sealed class KnowledgeResources
{
    [McpServerResource(UriTemplate = "cs1://knowledge/{topic}", Name = "Game mechanics")]
    private static string GameMechanics() => File.ReadAllText(KnowledgePath("05-game-mechanics.md"));

    // ... 06-expansions.md, 01-architecture.md, 07-verification.md
}
```

The agent then pulls a page only when it needs it, and opencode's built-in
`read_mcp_resource` handles truncation-to-file for oversized pages.

This is a good improvement, but it is **not required** for the system to work — the agent
can read `docs/` directly from the filesystem.

---

## 6. Running the game

The bridge only exists while the game is running with a city loaded.

```powershell
# Resume the newest save
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-resume.ps1

# Or start a fresh map
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-new-map.ps1
```

These come from the upstream project and hard-code a Steam path; adjust for an Epic
install if needed.
