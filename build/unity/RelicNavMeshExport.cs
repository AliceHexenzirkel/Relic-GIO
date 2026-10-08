// Bakes the navmesh of the open scene with the GIO server's settings and exports the raw Unity tiles, one
// file per 1024 m block, for build/navmesh_tool.py fromunity (which turns them into the server's files).
//
// Unity 2017.4 (the client's engine generation; its tiles are the server's tiles minus the region ids
// the tool adds). Put this file under Assets/Editor/ of a project that holds the scene geometry
// (meshes + terrains at their world positions), then:
//
//   Unity.exe -batchmode -nographics -quit -projectPath <project> -executeMethod RelicNavMeshExport.Run
//             -logFile <log>
//
// Environment: RELIC_NAVMESH_SCENE = the scene asset to open (Assets/.../x.unity; empty = the open scene),
// RELIC_NAVMESH_OUT = output folder (default <project>/navmesh_export), RELIC_NAVMESH_BLOCKS = the blocks to
// bake as "bx,by;bx,by" (default: every block the geometry touches), RELIC_NAVMESH_LAYERS = layer mask of the
// geometry to use (default: everything), RELIC_NAVMESH_GEOMETRY = RenderMeshes (default) or PhysicsColliders.
//
// Output per block: <out>/block_<bx>_<by>.tiles (int32 count, then per tile int32 size + bytes) and
// <out>/export.json (the settings, bounds, blocks and tile counts).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

public static class RelicNavMeshExport
{
    const float Block = 1024f;     // the server's block edge: tiles 64*bx .. 64*bx+63 at 16 m each
    const float Margin = 0.2f;     // the vendor's block bounds overhang

    public static void Run()
    {
        string scenePath = Environment.GetEnvironmentVariable("RELIC_NAVMESH_SCENE");
        string outDir = Environment.GetEnvironmentVariable("RELIC_NAVMESH_OUT");
        if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(Directory.GetCurrentDirectory(), "navmesh_export");
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        try
        {
            if (!string.IsNullOrEmpty(scenePath)) EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            // The server's bake settings, on the default agent type (every server file carries agentTypeID 0).
            var settings = NavMesh.GetSettingsByID(0);
            settings.agentRadius = 0.25f;
            settings.agentHeight = 1.6f;
            settings.agentSlope = 60f;
            settings.agentClimb = 0.4f;
            settings.minRegionArea = 36f;
            settings.overrideVoxelSize = true;
            settings.voxelSize = 0.125f;
            settings.overrideTileSize = true;
            settings.tileSize = 128;

            int layers = ~0;
            string layerText = Environment.GetEnvironmentVariable("RELIC_NAVMESH_LAYERS");
            if (!string.IsNullOrEmpty(layerText)) layers = int.Parse(layerText);
            var geometry = NavMeshCollectGeometry.RenderMeshes;
            if (Environment.GetEnvironmentVariable("RELIC_NAVMESH_GEOMETRY") == "PhysicsColliders") geometry = NavMeshCollectGeometry.PhysicsColliders;

            var sources = new List<NavMeshBuildSource>();
            var markups = new List<NavMeshBuildMarkup>();
            var world = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));
            NavMeshBuilder.CollectSources(world, layers, geometry, 0, markups, sources);
            if (sources.Count == 0) throw new Exception("no geometry sources in the scene (layers " + layers + ", " + geometry + ")");

            // Which blocks: given, or every block a source touches.
            var blocks = new List<int[]>();
            string blockText = Environment.GetEnvironmentVariable("RELIC_NAVMESH_BLOCKS");
            if (!string.IsNullOrEmpty(blockText))
            {
                foreach (var part in blockText.Split(';'))
                {
                    var xy = part.Split(',');
                    blocks.Add(new[] { int.Parse(xy[0].Trim()), int.Parse(xy[1].Trim()) });
                }
            }
            else
            {
                var seen = new HashSet<string>();
                foreach (var s in sources)
                {
                    Bounds b = SourceBounds(s);
                    int x0 = Mathf.FloorToInt(b.min.x / Block), x1 = Mathf.FloorToInt(b.max.x / Block);
                    int z0 = Mathf.FloorToInt(b.min.z / Block), z1 = Mathf.FloorToInt(b.max.z / Block);
                    for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
                        if (seen.Add(x + "," + z)) blocks.Add(new[] { x, z });
                }
            }
            report.Append("{\"settings\":{\"agentTypeID\":0,\"agentRadius\":0.25,\"agentHeight\":1.6,\"agentSlope\":60,\"agentClimb\":0.4,\"minRegionArea\":36,\"cellSize\":0.125,\"tileSize\":128},");
            report.Append("\"geometry\":\"" + geometry + "\",\"sources\":" + sources.Count + ",\"blocks\":[");
            bool first = true;
            foreach (var bl in blocks)
            {
                int bx = bl[0], by = bl[1];
                var center = new Vector3(bx * Block + Block / 2f, 500f, by * Block + Block / 2f);
                var size = new Vector3(Block + 2 * Margin, 3000f, Block + 2 * Margin);
                NavMeshData data = NavMeshBuilder.BuildNavMeshData(settings, sources, new Bounds(center, size), Vector3.zero, Quaternion.identity);
                if (data == null) throw new Exception("BuildNavMeshData returned null for block " + bx + "," + by);
                var so = new SerializedObject(data);
                var tiles = so.FindProperty("m_NavMeshTiles");
                if (tiles == null) throw new Exception("m_NavMeshTiles not found in NavMeshData");
                string file = Path.Combine(outDir, "block_" + bx + "_" + by + ".tiles");
                long bytes = 0;
                using (var w = new BinaryWriter(new FileStream(file, FileMode.Create)))
                {
                    w.Write(tiles.arraySize);
                    for (int i = 0; i < tiles.arraySize; i++)
                    {
                        var mesh = tiles.GetArrayElementAtIndex(i).FindPropertyRelative("m_MeshData");
                        var buf = new byte[mesh.arraySize];
                        for (int b = 0; b < buf.Length; b++) buf[b] = (byte)mesh.GetArrayElementAtIndex(b).intValue;
                        w.Write(buf.Length); w.Write(buf); bytes += buf.Length;
                    }
                }
                report.Append((first ? "" : ",") + "{\"x\":" + bx + ",\"y\":" + by + ",\"tiles\":" + tiles.arraySize + ",\"bytes\":" + bytes
                    + ",\"center\":[" + F(data.sourceBounds.center.x) + "," + F(data.sourceBounds.center.y) + "," + F(data.sourceBounds.center.z) + "]"
                    + ",\"extents\":[" + F(data.sourceBounds.extents.x) + "," + F(data.sourceBounds.extents.y) + "," + F(data.sourceBounds.extents.z) + "]}");
                first = false;
                UnityEngine.Object.DestroyImmediate(data);
            }
            report.Append("],\"ok\":true}");
        }
        catch (Exception e)
        {
            report.Length = 0;
            report.Append("{\"ok\":false,\"error\":" + Quote(e.ToString()) + "}");
        }
        File.WriteAllText(Path.Combine(outDir, "export.json"), report.ToString());
    }

    static Bounds SourceBounds(NavMeshBuildSource s)
    {
        // Enough for choosing blocks: the transform's position with the source's own extent when it has one.
        Vector3 pos = s.transform.GetColumn(3);
        if (s.shape == NavMeshBuildSourceShape.Mesh && s.sourceObject is Mesh)
        {
            var m = (Mesh)s.sourceObject;
            var b = m.bounds;
            var min = s.transform.MultiplyPoint3x4(b.min); var max = s.transform.MultiplyPoint3x4(b.max);
            var r = new Bounds(min, Vector3.zero); r.Encapsulate(max); return r;
        }
        if (s.shape == NavMeshBuildSourceShape.Terrain && s.sourceObject is TerrainData)
        {
            var t = (TerrainData)s.sourceObject;
            return new Bounds(pos + t.size / 2f, t.size);
        }
        return new Bounds(pos, s.size);
    }

    static string F(float v) { return v.ToString("R", System.Globalization.CultureInfo.InvariantCulture); }
    static string Quote(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\""; }
}
