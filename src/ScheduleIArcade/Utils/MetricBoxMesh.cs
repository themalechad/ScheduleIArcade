using UnityEngine;

namespace ScheduleIArcade.Utils;

/// <summary>
/// Unit cube meshes whose UVs are in metres of the final (scaled) size. Unity's primitive cube
/// stretches 0..1 UVs over every face, which smears tiled game materials; with metric UVs a
/// game floor/wall/skirting material tiles at the same density it has on the game's own meshes.
/// </summary>
internal static class MetricBoxMesh
{
    public static Mesh Create(Vector3 size, string name)
    {
        // Face definitions: normal, U axis, V axis (unit cube centred at the origin).
        Vector3[] normals = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        Vector3[] us = { Vector3.forward, Vector3.back, Vector3.right, Vector3.right, Vector3.left, Vector3.right };
        Vector3[] vs = { Vector3.up, Vector3.up, Vector3.forward, Vector3.back, Vector3.up, Vector3.up };

        var vertices = new Vector3[24];
        var uv = new Vector2[24];
        var meshNormals = new Vector3[24];
        var triangles = new int[36];
        for (int f = 0; f < 6; f++)
        {
            Vector3 n = normals[f], u = us[f], v = vs[f];
            float uMetres = Mathf.Abs(Vector3.Dot(u, size));
            float vMetres = Mathf.Abs(Vector3.Dot(v, size));
            for (int c = 0; c < 4; c++)
            {
                float su = c == 1 || c == 2 ? 0.5f : -0.5f;
                float sv = c >= 2 ? 0.5f : -0.5f;
                int index = f * 4 + c;
                vertices[index] = n * 0.5f + u * su + v * sv;
                uv[index] = new Vector2((su + 0.5f) * uMetres, (sv + 0.5f) * vMetres);
                meshNormals[index] = n;
            }

            // Unity's front-face normal for triangle (a,b,c) is Cross(b-a, c-a). Order each face so
            // that normal points outward (along n).
            bool uvMatchesNormal = Vector3.Dot(Vector3.Cross(u, v), n) > 0f;
            int b = f * 4, t = f * 6;
            if (!uvMatchesNormal)
            {
                triangles[t] = b; triangles[t + 1] = b + 2; triangles[t + 2] = b + 1;
                triangles[t + 3] = b; triangles[t + 4] = b + 3; triangles[t + 5] = b + 2;
            }
            else
            {
                triangles[t] = b; triangles[t + 1] = b + 1; triangles[t + 2] = b + 2;
                triangles[t + 3] = b; triangles[t + 4] = b + 2; triangles[t + 5] = b + 3;
            }
        }

        var mesh = new Mesh { name = name };
        mesh.vertices = vertices;
        mesh.normals = meshNormals;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    /// <summary>Planar XZ UVs in metres for a horizontal mesh (floors and ceilings).</summary>
    public static void ApplyPlanarMetricUv(Mesh mesh)
    {
        Vector3[] vertices = mesh.vertices;
        var uv = new Vector2[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            uv[i] = new Vector2(vertices[i].x, vertices[i].z);
        mesh.uv = uv;
        mesh.RecalculateTangents();
    }
}
