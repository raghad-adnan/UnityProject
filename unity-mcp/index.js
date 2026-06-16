#!/usr/bin/env node
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

const UNITY = "http://127.0.0.1:6400";

async function callUnity(action, params = {}) {
  try {
    const res = await fetch(`${UNITY}/execute`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ action, ...params }),
      signal: AbortSignal.timeout(15000),
    });
    return await res.json();
  } catch (err) {
    return { success: false, error: String(err) };
  }
}

function text(data) {
  const str = typeof data === "string" ? data : JSON.stringify(data, null, 2);
  return { content: [{ type: "text", text: str }] };
}

const server = new McpServer({ name: "unity-mcp", version: "1.0.0" });

// ─── Tools ────────────────────────────────────────────────────────────────────

server.tool(
  "unity_status",
  "Check if Unity Editor is running and the MCP plugin is active.",
  {},
  async () => {
    try {
      const res = await fetch(`${UNITY}/status`, { signal: AbortSignal.timeout(3000) });
      return text(await res.json());
    } catch {
      return text({ connected: false, message: "Unity is not running or the UnityMCPServer plugin is not loaded." });
    }
  }
);

server.tool(
  "unity_get_hierarchy",
  "Get the full scene hierarchy: all GameObjects with their names, positions, rotations, scales, components, and children.",
  {},
  async () => text(await callUnity("get_hierarchy"))
);

server.tool(
  "unity_get_project_structure",
  "List files and folders inside the Unity Assets directory.",
  {
    path: z.string().optional().describe("Subfolder to list, e.g. 'Scripts'. Leave empty for the root Assets folder."),
  },
  async ({ path }) => text(await callUnity("get_project_structure", { path: path ?? "" }))
);

server.tool(
  "unity_create_gameobject",
  "Create a new GameObject in the active Unity scene.",
  {
    name: z.string().describe("Name for the new GameObject"),
    primitive: z
      .enum(["Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad", "Empty"])
      .default("Empty")
      .describe("3D primitive shape. Use 'Empty' for an empty GameObject."),
    position: z.array(z.number()).length(3).optional().describe("[x, y, z] world position"),
    rotation: z.array(z.number()).length(3).optional().describe("[x, y, z] euler angles in degrees"),
    scale:    z.array(z.number()).length(3).optional().describe("[x, y, z] scale"),
  },
  async (args) => text(await callUnity("create_gameobject", args))
);

server.tool(
  "unity_modify_gameobject",
  "Modify an existing GameObject: rename it, move it, rotate it, scale it, or toggle its active state. Use unity_get_hierarchy to find the instanceId.",
  {
    instanceId: z.number().int().describe("Instance ID of the GameObject (from unity_get_hierarchy)"),
    name:     z.string().optional().describe("New name"),
    position: z.array(z.number()).length(3).optional().describe("[x, y, z] new world position"),
    rotation: z.array(z.number()).length(3).optional().describe("[x, y, z] new euler angles in degrees"),
    scale:    z.array(z.number()).length(3).optional().describe("[x, y, z] new scale"),
    active:   z.boolean().optional().describe("Set active (true) or inactive (false)"),
  },
  async ({ active, ...rest }) => {
    // Convert boolean active → activeStr because Unity JsonUtility cannot distinguish false from unset
    const params = { ...rest };
    if (active !== undefined) params.activeStr = active ? "true" : "false";
    return text(await callUnity("modify_gameobject", params));
  }
);

server.tool(
  "unity_delete_gameobject",
  "Delete a GameObject from the scene. This action is undoable in the Unity Editor (Ctrl+Z).",
  {
    instanceId: z.number().int().describe("Instance ID of the GameObject to delete"),
  },
  async ({ instanceId }) => text(await callUnity("delete_gameobject", { instanceId }))
);

server.tool(
  "unity_create_script",
  "Create or overwrite a C# script file in the Assets folder. The editor will recompile automatically.",
  {
    path:    z.string().describe("Path relative to Assets, e.g. 'Scripts/PlayerController.cs'"),
    content: z.string().describe("Full C# source code"),
  },
  async ({ path, content }) => text(await callUnity("create_script", { path, content }))
);

server.tool(
  "unity_read_file",
  "Read the text content of any file in the Assets folder (scripts, configs, etc.).",
  {
    path: z.string().describe("Path relative to Assets, e.g. 'Scripts/PlayerController.cs'"),
  },
  async ({ path }) => text(await callUnity("read_file", { path }))
);

// ─── Start ────────────────────────────────────────────────────────────────────

const transport = new StdioServerTransport();
await server.connect(transport);
