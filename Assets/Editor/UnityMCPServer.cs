// Unity 6 deprecated GetInstanceID/InstanceIDToObject in favour of EntityId (ECS).
// Our MCP JSON protocol uses int IDs, so we keep the old API and suppress the warnings.
#pragma warning disable CS0618
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// HTTP server that runs inside the Unity Editor, allowing Claude Code (via MCP) to
/// create/modify GameObjects, read the scene hierarchy, and manage scripts.
/// Runs automatically when Unity opens. Port 6400.
/// </summary>
[InitializeOnLoad]
public static class UnityMCPServer
{
    const int PORT = 6400;

    static HttpListener _listener;
    static Thread _thread;
    static readonly ConcurrentQueue<(string id, string body)> _queue = new();
    static readonly ConcurrentDictionary<string, string> _done = new();

    static UnityMCPServer()
    {
        EditorApplication.update += Tick;
        EditorApplication.quitting += Shutdown;
        Boot();
    }

    static void Boot()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{PORT}/");
            _listener.Start();
            _thread = new Thread(Loop) { IsBackground = true, Name = "UnityMCP" };
            _thread.Start();
            Debug.Log($"[UnityMCP] Ready on http://127.0.0.1:{PORT}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[UnityMCP] Failed to start: {ex.Message}");
        }
    }

    static void Shutdown()
    {
        _listener?.Stop();
        _listener = null;
    }

    static void Loop()
    {
        while (_listener != null && _listener.IsListening)
        {
            try
            {
                var ctx = _listener.GetContext();
                ThreadPool.QueueUserWorkItem(_ => Serve(ctx));
            }
            catch { }
        }
    }

    static void Serve(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.ContentType = "application/json; charset=utf-8";
        res.Headers.Add("Access-Control-Allow-Origin", "*");

        string body;

        if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/status")
        {
            body = "{\"ok\":true,\"port\":" + PORT + "}";
        }
        else if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/execute")
        {
            using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
            string payload = sr.ReadToEnd();
            string id = Guid.NewGuid().ToString("N");

            _queue.Enqueue((id, payload));

            var deadline = DateTime.UtcNow.AddSeconds(15);
            string result = null;
            while (DateTime.UtcNow < deadline)
            {
                if (_done.TryRemove(id, out result)) break;
                Thread.Sleep(16);
            }
            body = result ?? Fail("Timeout waiting for Unity main thread");
        }
        else
        {
            body = Fail("Not found");
            res.StatusCode = 404;
        }

        var buf = Encoding.UTF8.GetBytes(body);
        res.ContentLength64 = buf.Length;
        res.OutputStream.Write(buf, 0, buf.Length);
        res.Close();
    }

    static void Tick()
    {
        while (_queue.TryDequeue(out var item))
        {
            string result;
            try { result = Run(item.body); }
            catch (Exception ex) { result = Fail(ex.Message); }
            _done[item.id] = result;
        }
    }

    [Serializable]
    class Cmd
    {
        public string action    = "";
        public string name      = "";
        public string primitive = "Empty";
        public float[] position;
        public float[] rotation;
        public float[] scale;
        public int instanceId;
        public string path      = "";
        public string content   = "";
        public string activeStr = "";
    }

    static string Run(string json)
    {
        var c = JsonUtility.FromJson<Cmd>(json);
        return c.action switch
        {
            "create_gameobject"     => DoCreate(c),
            "get_hierarchy"         => DoHierarchy(),
            "get_project_structure" => DoStructure(c.path),
            "modify_gameobject"     => DoModify(c),
            "delete_gameobject"     => DoDelete(c.instanceId),
            "create_script"         => DoCreateScript(c.path, c.content),
            "read_file"             => DoReadFile(c.path),
            _ => Fail($"Unknown action '{c.action}'")
        };
    }

    static string DoCreate(Cmd c)
    {
        GameObject go;

        if (string.IsNullOrEmpty(c.primitive) || c.primitive == "Empty")
        {
            go = new GameObject(c.name);
        }
        else
        {
            PrimitiveType pt = c.primitive switch
            {
                "Sphere"   => PrimitiveType.Sphere,
                "Capsule"  => PrimitiveType.Capsule,
                "Cylinder" => PrimitiveType.Cylinder,
                "Plane"    => PrimitiveType.Plane,
                "Quad"     => PrimitiveType.Quad,
                _          => PrimitiveType.Cube
            };
            go = GameObject.CreatePrimitive(pt);
            go.name = c.name;
        }

        ApplyTransform(go, c);
        Undo.RegisterCreatedObjectUndo(go, $"MCP Create {go.name}");
        EditorSceneManager.MarkSceneDirty(go.scene);

        return Ok($"{{\"instanceId\":{go.GetInstanceID()},\"name\":{Q(go.name)}}}");
    }

    static string DoHierarchy()
    {
        var scene = SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();
        var sb = new StringBuilder("[");
        for (int i = 0; i < roots.Length; i++)
        {
            if (i > 0) sb.Append(',');
            AppendGO(sb, roots[i]);
        }
        sb.Append(']');
        return Ok(sb.ToString());
    }

    static void AppendGO(StringBuilder sb, GameObject go)
    {
        var p = go.transform.position;
        var r = go.transform.eulerAngles;
        var s = go.transform.localScale;

        sb.Append('{');
        sb.Append($"\"name\":{Q(go.name)},");
        sb.Append($"\"instanceId\":{go.GetInstanceID()},");
        sb.Append($"\"active\":{B(go.activeSelf)},");
        sb.Append($"\"tag\":{Q(go.tag)},");
        sb.Append($"\"position\":[{F(p.x)},{F(p.y)},{F(p.z)}],");
        sb.Append($"\"rotation\":[{F(r.x)},{F(r.y)},{F(r.z)}],");
        sb.Append($"\"scale\":[{F(s.x)},{F(s.y)},{F(s.z)}],");

        var comps = go.GetComponents<Component>();
        sb.Append("\"components\":[");
        for (int i = 0; i < comps.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(comps[i] != null ? Q(comps[i].GetType().Name) : "null");
        }
        sb.Append("],\"children\":[");
        for (int i = 0; i < go.transform.childCount; i++)
        {
            if (i > 0) sb.Append(',');
            AppendGO(sb, go.transform.GetChild(i).gameObject);
        }
        sb.Append("]}");
    }

    static string DoStructure(string rootPath)
    {
        if (string.IsNullOrEmpty(rootPath)) rootPath = "Assets";
        if (!System.IO.Directory.Exists(rootPath))
            return Fail($"Path not found: {rootPath}");

        var sb = new StringBuilder();
        BuildTree(sb, rootPath, 0);
        return Ok(Q(sb.ToString()));
    }

    static void BuildTree(StringBuilder sb, string path, int depth)
    {
        string indent = new string(' ', depth * 2);
        sb.AppendLine(indent + System.IO.Path.GetFileName(path) + "/");
        foreach (var dir in System.IO.Directory.GetDirectories(path))
            BuildTree(sb, dir, depth + 1);
        foreach (var file in System.IO.Directory.GetFiles(path))
        {
            if (!file.EndsWith(".meta"))
                sb.AppendLine(indent + "  " + System.IO.Path.GetFileName(file));
        }
    }

    static string DoModify(Cmd c)
    {
        var go = EditorUtility.InstanceIDToObject(c.instanceId) as GameObject;
        if (go == null) return Fail($"GameObject with instanceId {c.instanceId} not found");

        ApplyTransform(go, c);

        if (c.activeStr == "true")  go.SetActive(true);
        if (c.activeStr == "false") go.SetActive(false);
        if (!string.IsNullOrEmpty(c.name)) go.name = c.name;

        EditorUtility.SetDirty(go);
        EditorSceneManager.MarkSceneDirty(go.scene);
        return Ok($"{{\"instanceId\":{go.GetInstanceID()},\"name\":{Q(go.name)}}}");
    }

    static string DoDelete(int instanceId)
    {
        var go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
        if (go == null) return Fail($"GameObject with instanceId {instanceId} not found");

        string name = go.name;
        Undo.DestroyObjectImmediate(go);
        return Ok($"{{\"deleted\":{Q(name)}}}");
    }

    static string DoCreateScript(string path, string content)
    {
        if (string.IsNullOrEmpty(path)) return Fail("path is required");
        string dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            System.IO.Directory.CreateDirectory(dir);

        System.IO.File.WriteAllText(path, content, Encoding.UTF8);
        AssetDatabase.ImportAsset(path);
        return Ok($"{{\"created\":{Q(path)}}}");
    }

    static string DoReadFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return Fail("path is required");
        if (!System.IO.File.Exists(path)) return Fail($"File not found: {path}");
        string content = System.IO.File.ReadAllText(path, Encoding.UTF8);
        return Ok(Q(content));
    }

    static void ApplyTransform(GameObject go, Cmd c)
    {
        if (c.position != null && c.position.Length == 3)
            go.transform.position = new Vector3(c.position[0], c.position[1], c.position[2]);
        if (c.rotation != null && c.rotation.Length == 3)
            go.transform.eulerAngles = new Vector3(c.rotation[0], c.rotation[1], c.rotation[2]);
        if (c.scale != null && c.scale.Length == 3)
            go.transform.localScale = new Vector3(c.scale[0], c.scale[1], c.scale[2]);
    }

    static string Ok(string data)   => $"{{\"ok\":true,\"data\":{data}}}";
    static string Fail(string msg)  => $"{{\"ok\":false,\"error\":{Q(msg)}}}";
    static string Q(string s)       => "\"" + s.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\n","\\n").Replace("\r","\\r") + "\"";
    static string F(float v)        => v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
    static string B(bool v)         => v ? "true" : "false";
}
