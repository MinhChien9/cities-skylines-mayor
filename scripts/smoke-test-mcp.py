#!/usr/bin/env python3
"""
Smoke test for the CS1 Mayor MCP server.

Drives the server over stdio exactly the way an MCP client does:
newline-delimited JSON-RPC 2.0 messages on stdin/stdout.

Verifies:
  1. the server answers `initialize` with a valid result
  2. `tools/list` returns the expected tool surface
  3. a tool call fails gracefully when the game is not running (no crash)

Usage:
    python scripts/smoke-test-mcp.py [--server PATH_TO_DLL]
"""

import json
import os
import subprocess
import sys
import threading
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_DLL = os.path.join(REPO, "mcp-server", "bin", "Debug", "net10.0", "Cs1Mayor.Mcp.dll")

EXPECTED_TOOLS = {
    "city_snapshot",
    "city_problems",
    "city_districts",
    "city_infrastructure",
    "city_services",
    "city_prefabs",
    "city_build_road",
    "city_zone",
    "city_place_building",
    "city_bulldoze",
    "city_economy",
    "city_save",
    "city_health",
}


def find_server():
    for candidate in (
        os.path.join(REPO, "mcp-server", "bin", "Debug", "net10.0", "Cs1Mayor.Mcp.dll"),
        os.path.join(REPO, "mcp-server", "bin", "Release", "net10.0", "Cs1Mayor.Mcp.dll"),
    ):
        if os.path.exists(candidate):
            return candidate
    return None


class McpClient:
    def __init__(self, dll_path):
        self.proc = subprocess.Popen(
            ["dotnet", dll_path],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            bufsize=1,
        )
        self.next_id = 0
        self.stderr_lines = []
        threading.Thread(target=self._drain_stderr, daemon=True).start()

    def _drain_stderr(self):
        for line in self.proc.stderr:
            self.stderr_lines.append(line.rstrip())

    def send(self, method, params=None):
        self.next_id += 1
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        if params is not None:
            msg["params"] = params
        self.proc.stdin.write(json.dumps(msg) + "\n")
        self.proc.stdin.flush()
        return self.next_id

    def notify(self, method, params=None):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        self.proc.stdin.write(json.dumps(msg) + "\n")
        self.proc.stdin.flush()

    def read(self, timeout=20.0):
        """Reads one newline-delimited JSON-RPC message from stdout.

        Uses a reader thread because stdout is a pipe and readline() would otherwise
        block forever if the server does not answer.
        """
        box = {}

        def _read():
            try:
                box["line"] = self.proc.stdout.readline()
            except Exception as exc:
                box["error"] = exc

        t = threading.Thread(target=_read, daemon=True)
        t.start()
        t.join(timeout)
        if t.is_alive():
            raise TimeoutError(
                "Timed out waiting for a response from the MCP server. "
                f"stderr so far: {self.stderr_lines[-10:]}")
        if "error" in box:
            raise RuntimeError(f"Failed to read from the MCP server: {box['error']}")
        line = box.get("line", "")
        if not line.strip():
            raise RuntimeError(
                "The MCP server closed stdout unexpectedly. "
                f"stderr: {self.stderr_lines[-10:]}")
        return json.loads(line)

    def close(self):
        try:
            self.proc.stdin.close()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=5)
        except Exception:
            self.proc.kill()


def main():
    dll = sys.argv[sys.argv.index("--server") + 1] if "--server" in sys.argv else find_server()
    if not dll:
        print("FAIL: could not find Cs1Mayor.Mcp.dll - build the MCP server first:")
        print("  dotnet build mcp-server/Cs1Mayor.Mcp.csproj")
        return 1

    print(f"Server: {dll}")
    client = McpClient(dll)
    failures = []

    try:
        # 1. initialize
        client.send("initialize", {
            "protocolVersion": "2025-06-18",
            "capabilities": {"roots": {}},
            "clientInfo": {"name": "smoke-test", "version": "1.0"},
        })
        resp = client.read()
        if "error" in resp:
            failures.append(f"initialize returned an error: {resp['error']}")
        else:
            result = resp.get("result", {})
            print(f"  initialize OK: protocol={result.get('protocolVersion')} "
                  f"server={result.get('serverInfo', {}).get('name')}")
            if not result.get("serverInfo"):
                failures.append("initialize result is missing serverInfo")
            if "tools" not in result.get("capabilities", {}):
                failures.append("initialize result does not advertise the tools capability")

        client.notify("notifications/initialized")

        # 2. tools/list
        client.send("tools/list", {})
        resp = client.read()
        tools = resp.get("result", {}).get("tools", [])
        names = {t.get("name") for t in tools}
        print(f"  tools/list OK: {len(tools)} tools")
        missing = EXPECTED_TOOLS - names
        if missing:
            failures.append(f"missing tools: {sorted(missing)}")
        for tool in tools:
            if not tool.get("description"):
                failures.append(f"tool {tool.get('name')} has no description")
            schema = tool.get("inputSchema", {})
            if schema.get("type") != "object":
                failures.append(f"tool {tool.get('name')} has a non-object inputSchema")

        # 3. a tool call with the game NOT running must degrade gracefully
        client.send("tools/call", {"name": "city_snapshot", "arguments": {}})
        resp = client.read()
        if "error" in resp:
            failures.append(f"tools/call returned a protocol error: {resp['error']}")
        else:
            content = resp.get("result", {}).get("content", [])
            text = content[0].get("text", "") if content else ""
            if text:
                try:
                    payload = json.loads(text)
                    ok = payload.get("ok")
                    print(f"  tools/call OK (game offline): ok={ok}")
                except json.JSONDecodeError:
                    print("  tools/call OK (game offline): non-JSON text response")
            else:
                failures.append("tools/call returned no content")

    except Exception as exc:
        failures.append(f"exception: {type(exc).__name__}: {exc}")
    finally:
        client.close()

    if failures:
        print("\nFAILURES:")
        for f in failures:
            print(f"  - {f}")
        return 1

    print("\nALL CHECKS PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
