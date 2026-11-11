using UnityEngine;

namespace ScheduleIArcade.Utils;

/// <summary>
/// Connected-piece ("island") operations on the packaged raw meshes. Two triangles belong to the same
/// island when they share a vertex index or a vertex position (the exporter splits vertices on UV
/// seams), so a removed prop always goes as a whole and never tears the room shell.
/// </summary>
internal static class MeshIslands
{
    public readonly struct Island
    {
        public Island(List<int> triangleStarts, Vector3 min, Vector3 max)
        {
            TriangleStarts = triangleStarts;
            this.min = min;
            this.max = max;
        }

        public List<int> TriangleStarts { get; }
        public readonly Vector3 min;
        public readonly Vector3 max;
    }

    public static List<Island> Find(Vector3[] vertices, int[] triangles)
    {
        int n = vertices.Length;
        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;
        int Root(int a)
        {
            while (parent[a] != a)
            {
                parent[a] = parent[parent[a]];
                a = parent[a];
            }
            return a;
        }
        void Union(int a, int b)
        {
            a = Root(a);
            b = Root(b);
            if (a != b)
                parent[a] = b;
        }

        var byPosition = new Dictionary<(int, int, int), int>();
        for (int i = 0; i < n; i++)
        {
            Vector3 v = vertices[i];
            var key = (Mathf.RoundToInt(v.x * 10000f), Mathf.RoundToInt(v.y * 10000f), Mathf.RoundToInt(v.z * 10000f));
            if (byPosition.TryGetValue(key, out int other))
                Union(i, other);
            else
                byPosition[key] = i;
        }
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            Union(triangles[t], triangles[t + 1]);
            Union(triangles[t + 1], triangles[t + 2]);
        }

        var groups = new Dictionary<int, List<int>>();
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            int root = Root(triangles[t]);
            if (!groups.TryGetValue(root, out List<int>? list))
                groups[root] = list = new List<int>();
            list.Add(t);
        }

        var islands = new List<Island>(groups.Count);
        foreach (List<int> starts in groups.Values)
        {
            Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);
            foreach (int t in starts)
            {
                for (int k = 0; k < 3; k++)
                {
                    Vector3 v = vertices[triangles[t + k]];
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
            }
            islands.Add(new Island(starts, min, max));
        }
        return islands;
    }

    /// <summary>Removes every island whose bounds lie entirely inside one of the boxes.</summary>
    public static int[] RemoveIslandsInside(Vector3[] vertices, int[] triangles, (string label, Vector3 min, Vector3 max)[] boxes, out string removedSummary)
    {
        var removedTriangles = new HashSet<int>();
        var labels = new List<string>();
        foreach (Island island in Find(vertices, triangles))
        {
            foreach (var (label, min, max) in boxes)
            {
                bool inside = island.min.x >= min.x && island.min.y >= min.y && island.min.z >= min.z
                    && island.max.x <= max.x && island.max.y <= max.y && island.max.z <= max.z;
                if (!inside)
                    continue;
                foreach (int t in island.TriangleStarts)
                    removedTriangles.Add(t);
                labels.Add($"{label} ({island.TriangleStarts.Count} tris)");
                break;
            }
        }
        removedSummary = string.Join(", ", labels);
        if (removedTriangles.Count == 0)
            return triangles;
        var kept = new List<int>(triangles.Length);
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            if (removedTriangles.Contains(t))
                continue;
            kept.Add(triangles[t]);
            kept.Add(triangles[t + 1]);
            kept.Add(triangles[t + 2]);
        }
        return kept.ToArray();
    }

    /// <summary>
    /// Moves islands that <paramref name="classify"/> names into separate triangle lists (appended to
    /// <paramref name="parts"/>), returning the triangles that stay on the main mesh.
    /// </summary>
    public static int[] Split(Vector3[] vertices, int[] triangles, Func<Island, string?> classify, List<(string name, int[] triangles)> parts)
    {
        var byName = new Dictionary<string, List<int>>();
        var moved = new HashSet<int>();
        foreach (Island island in Find(vertices, triangles))
        {
            string? name = classify(island);
            if (name == null)
                continue;
            if (!byName.TryGetValue(name, out List<int>? list))
                byName[name] = list = new List<int>();
            foreach (int t in island.TriangleStarts)
            {
                moved.Add(t);
                list.Add(triangles[t]);
                list.Add(triangles[t + 1]);
                list.Add(triangles[t + 2]);
            }
        }
        foreach (var pair in byName)
            parts.Add((pair.Key, pair.Value.ToArray()));
        if (moved.Count == 0)
            return triangles;
        var kept = new List<int>(triangles.Length);
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            if (moved.Contains(t))
                continue;
            kept.Add(triangles[t]);
            kept.Add(triangles[t + 1]);
            kept.Add(triangles[t + 2]);
        }
        return kept.ToArray();
    }
}

/// <summary>
/// The two PAC-MAN logos printed along the cocktail table's long sides are mirrored on the packaged
/// mesh (their reading direction runs backwards relative to the letters' up direction, measured
/// from the table-top triangles' UV-to-surface mapping). Flipping the texture V coordinate about
/// each logo's own centre on the top-face triangles mirrors the reading axis only, so each logo reads
/// left-to-right, upright, from its own side of the table. Affected vertices are duplicated so the
/// neighbouring bevel/side faces keep their original mapping.
/// </summary>
internal static class PacmanLogoFix
{
    // (triangle-centroid UV rect, logo text V range) for the two logos in tabletop_base_texture.jpg.
    private static readonly (Rect select, float vMin, float vMax)[] Logos =
    {
        (Rect.MinMaxRect(0.55f, 0.38f, 0.76f, 0.82f), 0.414f, 0.717f),
        (Rect.MinMaxRect(0.80f, 0.55f, 1.00f, 1.00f), 0.6875f, 0.980f),
    };

    public static int FlipLogoReadingAxis(ref Vector3[] vertices, ref Vector3[] normals, ref Vector2[] uvs, int[] triangles)
    {
        var newVertices = new List<Vector3>(vertices);
        var newNormals = new List<Vector3>(normals);
        var newUvs = new List<Vector2>(uvs);
        int flipped = 0;
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
            Vector3 faceNormal = Vector3.Cross(b - a, c - a);
            if (faceNormal.sqrMagnitude < 1e-12f || Mathf.Abs(faceNormal.normalized.y) < 0.9f || Mathf.Min(a.y, Mathf.Min(b.y, c.y)) < 0.25f)
                continue; // only the flat table top
            Vector2 centroid = (uvs[triangles[t]] + uvs[triangles[t + 1]] + uvs[triangles[t + 2]]) / 3f;
            foreach (var (select, vMin, vMax) in Logos)
            {
                if (!select.Contains(centroid))
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    int index = triangles[t + k];
                    Vector2 uv = uvs[index];
                    uv.y = Mathf.Clamp(vMin + vMax - uv.y, 0.005f, 0.995f);
                    newVertices.Add(vertices[index]);
                    newNormals.Add(normals[index]);
                    newUvs.Add(uv);
                    triangles[t + k] = newVertices.Count - 1;
                }
                flipped++;
                break;
            }
        }
        if (flipped > 0)
        {
            vertices = newVertices.ToArray();
            normals = newNormals.ToArray();
            uvs = newUvs.ToArray();
        }
        return flipped;
    }
}

/// <summary>Placement of the Pac-Man picture on the cocktail table (table-local metres).</summary>
internal static class PacmanScreenLayout
{
    /// <summary>Centre of the printed monitor inset, 4 mm above the table top.</summary>
    public static readonly Vector3 LocalCentre = new(-0.119f, 0.303f, -0.015f);
    /// <summary>Picture height along the table axis (288 px) and width across it (224 px).</summary>
    public const float Height = 0.524f;
    public const float Width = Height * ArcadeDimensions.PacManNativeWidth / ArcadeDimensions.PacManNativeHeight;

    /// <summary>
    /// Face-up quad whose texture top points away from the seated player, so the maze reads upright
    /// from that end of the table (cocktail cabinets flip the picture for each player's turn).
    /// </summary>
    public static Quaternion LocalRotation(bool seatAtPositiveX)
        => Quaternion.LookRotation(Vector3.down, seatAtPositiveX ? Vector3.left : Vector3.right);
}
