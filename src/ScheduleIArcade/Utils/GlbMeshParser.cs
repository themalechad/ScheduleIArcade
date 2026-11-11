using System.Globalization;
using System.Text;

namespace ScheduleIArcade.Utils;

/// <summary>
/// Minimal glTF 2.0 binary (.glb) mesh reader. No animations, skins, morph targets or
/// materials are loaded — only POSITION / NORMAL / TEXCOORD_0 and triangle indices, which
/// is enough to build a Unity Mesh as a fallback when a vanilla object is not in the scene.
/// This type has no Unity dependency so it can be unit-tested headlessly.
/// </summary>
internal sealed class ParsedGlbMesh
{
    public string Name { get; init; } = "Mesh";
    public float[] Positions { get; init; } = Array.Empty<float>(); // xyz, vertexCount * 3
    public float[]? Normals { get; init; }
    public float[]? Uvs { get; init; }
    public int[] Indices { get; init; } = Array.Empty<int>();
    public int VertexCount => Positions.Length / 3;
}

internal static class GlbMeshParser
{
    private const uint Magic = 0x46546C67; // 'glTF'
    private const uint JsonChunk = 0x4E4F534A; // 'JSON'
    private const uint BinChunk = 0x004E4942; // 'BIN\0'
    private const int ComponentByte = 5120, ComponentUByte = 5121, ComponentShort = 5122;
    private const int ComponentUShort = 5123, ComponentUInt = 5125, ComponentFloat = 5126;

    public static bool TryParse(byte[] bytes, out ParsedGlbMesh mesh, out string error)
    {
        mesh = new ParsedGlbMesh();
        error = string.Empty;
        try
        {
            if (bytes == null || bytes.Length < 20)
            {
                error = "File is too small to be a GLB.";
                return false;
            }
            if (ReadU32(bytes, 0) != Magic)
            {
                error = "Not a GLB file (missing glTF magic).";
                return false;
            }
            uint version = ReadU32(bytes, 4);
            if (version != 2)
            {
                error = "Unsupported glTF version " + version + " (need 2).";
                return false;
            }

            int offset = 12;
            byte[]? jsonBytes = null;
            byte[]? bin = null;
            while (offset + 8 <= bytes.Length)
            {
                int chunkLength = (int)ReadU32(bytes, offset);
                uint chunkType = ReadU32(bytes, offset + 4);
                offset += 8;
                if (chunkLength < 0 || offset + chunkLength > bytes.Length)
                {
                    error = "GLB chunk is truncated.";
                    return false;
                }
                var chunk = new byte[chunkLength];
                Buffer.BlockCopy(bytes, offset, chunk, 0, chunkLength);
                offset += chunkLength;
                if (chunkType == JsonChunk) jsonBytes = chunk;
                else if (chunkType == BinChunk) bin = chunk;
            }
            if (jsonBytes == null)
            {
                error = "GLB has no JSON chunk.";
                return false;
            }

            string json = Encoding.UTF8.GetString(jsonBytes).TrimEnd('\0');
            object? rootObj = MiniJson.Parse(json);
            var root = MiniJson.Obj(rootObj);
            if (root == null)
            {
                error = "GLB JSON is not an object.";
                return false;
            }

            var meshes = MiniJson.Arr(root, "meshes");
            if (meshes == null || meshes.Count == 0)
            {
                error = "GLB has no meshes.";
                return false;
            }

            var accessors = MiniJson.Arr(root, "accessors") ?? new List<object?>();
            var views = MiniJson.Arr(root, "bufferViews") ?? new List<object?>();
            var positions = new List<float>();
            var normals = new List<float>();
            var uvs = new List<float>();
            var indices = new List<int>();
            string name = "Mesh";
            bool anyNormal = false, anyUv = false;

            foreach (object? meshNode in meshes)
            {
                var meshObj = MiniJson.Obj(meshNode);
                if (meshObj == null) continue;
                string meshName = MiniJson.Str(meshObj, "name") ?? name;
                if (name == "Mesh") name = meshName;
                var primitives = MiniJson.Arr(meshObj, "primitives");
                if (primitives == null) continue;
                foreach (object? primNode in primitives)
                {
                    var prim = MiniJson.Obj(primNode);
                    if (prim == null) continue;
                    int mode = MiniJson.Int(prim, "mode", 4);
                    if (mode != 4)
                    {
                        error = "Unsupported primitive mode " + mode + " (only TRIANGLES).";
                        return false;
                    }
                    var attributes = MiniJson.Obj(prim, "attributes");
                    if (attributes == null || !attributes.ContainsKey("POSITION"))
                    {
                        error = "A primitive is missing POSITION.";
                        return false;
                    }

                    int baseVertex = positions.Count / 3;
                    float[] pos = ReadVec(bytes, bin, accessors, views, MiniJson.Int(attributes, "POSITION", -1), 3, normalizedDefault: false);
                    if (pos.Length < 3)
                    {
                        error = "POSITION accessor is empty.";
                        return false;
                    }
                    positions.AddRange(pos);

                    if (attributes.ContainsKey("NORMAL"))
                    {
                        float[] nrm = ReadVec(bytes, bin, accessors, views, MiniJson.Int(attributes, "NORMAL", -1), 3, false);
                        if (nrm.Length == pos.Length) { normals.AddRange(nrm); anyNormal = true; }
                        else normals.AddRange(new float[pos.Length]);
                    }
                    else normals.AddRange(new float[pos.Length]);

                    if (attributes.ContainsKey("TEXCOORD_0"))
                    {
                        float[] uv = ReadVec(bytes, bin, accessors, views, MiniJson.Int(attributes, "TEXCOORD_0", -1), 2, true);
                        if (uv.Length == (pos.Length / 3) * 2) { uvs.AddRange(uv); anyUv = true; }
                        else uvs.AddRange(new float[(pos.Length / 3) * 2]);
                    }
                    else uvs.AddRange(new float[(pos.Length / 3) * 2]);

                    if (prim.ContainsKey("indices"))
                    {
                        int[] tris = ReadIndices(bytes, bin, accessors, views, MiniJson.Int(prim, "indices", -1));
                        for (int i = 0; i < tris.Length; i++)
                            indices.Add(tris[i] + baseVertex);
                    }
                    else
                    {
                        int count = pos.Length / 3;
                        for (int i = 0; i < count; i++)
                            indices.Add(baseVertex + i);
                    }
                }
            }

            if (positions.Count < 9 || indices.Count < 3)
            {
                error = "GLB mesh has no triangles.";
                return false;
            }

            // glTF is right-handed Y-up; Unity is left-handed Y-up. Flip Z and triangle winding.
            float[] posArr = positions.ToArray();
            for (int i = 2; i < posArr.Length; i += 3)
                posArr[i] = -posArr[i];
            float[]? nrmArr = null;
            if (anyNormal)
            {
                nrmArr = normals.ToArray();
                for (int i = 2; i < nrmArr.Length; i += 3)
                    nrmArr[i] = -nrmArr[i];
            }
            int[] idxArr = indices.ToArray();
            for (int i = 0; i + 2 < idxArr.Length; i += 3)
            {
                (idxArr[i + 1], idxArr[i + 2]) = (idxArr[i + 2], idxArr[i + 1]);
            }

            mesh = new ParsedGlbMesh
            {
                Name = name,
                Positions = posArr,
                Normals = nrmArr,
                Uvs = anyUv ? uvs.ToArray() : null,
                Indices = idxArr
            };
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Builds a tiny valid GLB (one triangle) for tests. No ripped data.</summary>
    public static byte[] WriteTriangleGlb()
    {
        // Three vertices in the XY plane, one triangle, with normals and uvs.
        var bin = new MemoryStream();
        void F(float v)
        {
            bin.Write(BitConverter.GetBytes(v), 0, 4);
        }
        // positions (36 bytes)
        F(0f); F(0f); F(0f);
        F(1f); F(0f); F(0f);
        F(0f); F(1f); F(0f);
        // normals (36 bytes)
        F(0f); F(0f); F(1f);
        F(0f); F(0f); F(1f);
        F(0f); F(0f); F(1f);
        // uvs (24 bytes)
        F(0f); F(0f);
        F(1f); F(0f);
        F(0f); F(1f);
        // indices (6 bytes + 2 pad)
        bin.Write(BitConverter.GetBytes((ushort)0), 0, 2);
        bin.Write(BitConverter.GetBytes((ushort)1), 0, 2);
        bin.Write(BitConverter.GetBytes((ushort)2), 0, 2);
        bin.Write(new byte[2], 0, 2);
        byte[] binBytes = bin.ToArray();

        string json =
            "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":" + binBytes.Length +
            "}],\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36}," +
            "{\"buffer\":0,\"byteOffset\":36,\"byteLength\":36}," +
            "{\"buffer\":0,\"byteOffset\":72,\"byteLength\":24}," +
            "{\"buffer\":0,\"byteOffset\":96,\"byteLength\":6}]," +
            "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"max\":[1,1,0],\"min\":[0,0,0]}," +
            "{\"bufferView\":1,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"}," +
            "{\"bufferView\":2,\"componentType\":5126,\"count\":3,\"type\":\"VEC2\"}," +
            "{\"bufferView\":3,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}]," +
            "\"meshes\":[{\"name\":\"Triangle\",\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"TEXCOORD_0\":2},\"indices\":3}]}]," +
            "\"nodes\":[{\"mesh\":0}],\"scenes\":[{\"nodes\":[0]}],\"scene\":0}";
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
        int binPad = (4 - (binBytes.Length % 4)) % 4;
        int jsonChunkLen = jsonBytes.Length + jsonPad;
        int binChunkLen = binBytes.Length + binPad;
        int total = 12 + 8 + jsonChunkLen + 8 + binChunkLen;
        var output = new byte[total];
        WriteU32(output, 0, Magic);
        WriteU32(output, 4, 2);
        WriteU32(output, 8, (uint)total);
        WriteU32(output, 12, (uint)jsonChunkLen);
        WriteU32(output, 16, JsonChunk);
        Buffer.BlockCopy(jsonBytes, 0, output, 20, jsonBytes.Length);
        for (int i = 0; i < jsonPad; i++) output[20 + jsonBytes.Length + i] = 0x20;
        int binHeader = 20 + jsonChunkLen;
        WriteU32(output, binHeader, (uint)binChunkLen);
        WriteU32(output, binHeader + 4, BinChunk);
        Buffer.BlockCopy(binBytes, 0, output, binHeader + 8, binBytes.Length);
        return output;
    }

    private static float[] ReadVec(byte[] file, byte[]? bin, List<object?> accessors, List<object?> views, int accessorIndex, int components, bool normalizedDefault)
    {
        if (accessorIndex < 0 || accessorIndex >= accessors.Count)
            return Array.Empty<float>();
        var acc = MiniJson.Obj(accessors[accessorIndex]);
        if (acc == null) return Array.Empty<float>();
        int count = MiniJson.Int(acc, "count", 0);
        int typeCount = TypeCount(MiniJson.Str(acc, "type"));
        if (typeCount != components) components = typeCount;
        int componentType = MiniJson.Int(acc, "componentType", ComponentFloat);
        bool normalized = MiniJson.Bool(acc, "normalized", componentType != ComponentFloat && normalizedDefault);
        byte[] buffer = GetBuffer(file, bin, views, acc, out int byteOffset);
        var result = new float[count * components];
        int stride = ViewStride(views, acc, componentType, components);
        for (int i = 0; i < count; i++)
        {
            int at = byteOffset + i * stride;
            for (int c = 0; c < components; c++)
                result[i * components + c] = ReadComponent(buffer, at + c * ComponentSize(componentType), componentType, normalized);
        }
        return result;
    }

    private static int[] ReadIndices(byte[] file, byte[]? bin, List<object?> accessors, List<object?> views, int accessorIndex)
    {
        if (accessorIndex < 0 || accessorIndex >= accessors.Count)
            return Array.Empty<int>();
        var acc = MiniJson.Obj(accessors[accessorIndex]);
        if (acc == null) return Array.Empty<int>();
        int count = MiniJson.Int(acc, "count", 0);
        int componentType = MiniJson.Int(acc, "componentType", ComponentUShort);
        byte[] buffer = GetBuffer(file, bin, views, acc, out int byteOffset);
        var result = new int[count];
        int size = ComponentSize(componentType);
        for (int i = 0; i < count; i++)
            result[i] = (int)ReadComponent(buffer, byteOffset + i * size, componentType, false);
        return result;
    }

    private static byte[] GetBuffer(byte[] file, byte[]? bin, List<object?> views, Dictionary<string, object?> acc, out int byteOffset)
    {
        byteOffset = MiniJson.Int(acc, "byteOffset", 0);
        if (!acc.ContainsKey("bufferView"))
            return bin ?? file;
        int viewIndex = MiniJson.Int(acc, "bufferView", -1);
        if (viewIndex < 0 || viewIndex >= views.Count)
            return bin ?? file;
        var view = MiniJson.Obj(views[viewIndex]);
        if (view == null) return bin ?? file;
        byteOffset += MiniJson.Int(view, "byteOffset", 0);
        return bin ?? file;
    }

    private static int ViewStride(List<object?> views, Dictionary<string, object?> acc, int componentType, int components)
    {
        int tight = ComponentSize(componentType) * components;
        if (!acc.ContainsKey("bufferView")) return tight;
        int viewIndex = MiniJson.Int(acc, "bufferView", -1);
        if (viewIndex < 0 || viewIndex >= views.Count) return tight;
        var view = MiniJson.Obj(views[viewIndex]);
        int stride = view == null ? 0 : MiniJson.Int(view, "byteStride", 0);
        return stride > 0 ? stride : tight;
    }

    private static int TypeCount(string? type) => type switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        "MAT4" => 16,
        _ => 3
    };

    private static int ComponentSize(int componentType) => componentType switch
    {
        ComponentByte or ComponentUByte => 1,
        ComponentShort or ComponentUShort => 2,
        ComponentUInt or ComponentFloat => 4,
        _ => 4
    };

    private static float ReadComponent(byte[] buffer, int offset, int componentType, bool normalized)
    {
        if (offset < 0 || offset >= buffer.Length) return 0f;
        switch (componentType)
        {
            case ComponentByte:
                sbyte sb = (sbyte)buffer[offset];
                return normalized ? Math.Max(sb / 127f, -1f) : sb;
            case ComponentUByte:
                byte ub = buffer[offset];
                return normalized ? ub / 255f : ub;
            case ComponentShort:
                if (offset + 1 >= buffer.Length) return 0f;
                short s = BitConverter.ToInt16(buffer, offset);
                return normalized ? Math.Max(s / 32767f, -1f) : s;
            case ComponentUShort:
                if (offset + 1 >= buffer.Length) return 0f;
                ushort us = BitConverter.ToUInt16(buffer, offset);
                return normalized ? us / 65535f : us;
            case ComponentUInt:
                if (offset + 3 >= buffer.Length) return 0f;
                uint ui = BitConverter.ToUInt32(buffer, offset);
                return ui;
            default:
                if (offset + 3 >= buffer.Length) return 0f;
                return BitConverter.ToSingle(buffer, offset);
        }
    }

    private static uint ReadU32(byte[] b, int offset) => BitConverter.ToUInt32(b, offset);
    private static void WriteU32(byte[] b, int offset, uint value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, b, offset, 4);
    }
}

/// <summary>Tiny JSON reader (objects, arrays, strings, numbers, true/false/null). No Unity types.</summary>
internal static class MiniJson
{
    public static object? Parse(string json)
    {
        var r = new Reader(json);
        object? value = r.ParseValue();
        return value;
    }

    public static Dictionary<string, object?>? Obj(object? value) => value as Dictionary<string, object?>;
    public static Dictionary<string, object?>? Obj(Dictionary<string, object?> obj, string key) => obj.TryGetValue(key, out object? v) ? Obj(v) : null;
    public static List<object?>? Arr(object? value) => value as List<object?>;
    public static List<object?>? Arr(Dictionary<string, object?> obj, string key) => obj.TryGetValue(key, out object? v) ? Arr(v) : null;
    public static string? Str(Dictionary<string, object?> obj, string key) => obj.TryGetValue(key, out object? v) ? v as string ?? v?.ToString() : null;
    public static int Int(Dictionary<string, object?> obj, string key, int fallback)
    {
        if (!obj.TryGetValue(key, out object? v) || v == null) return fallback;
        if (v is int i) return i;
        if (v is long l) return (int)l;
        if (v is double d) return (int)d;
        return int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
    }
    public static bool Bool(Dictionary<string, object?> obj, string key, bool fallback)
    {
        if (!obj.TryGetValue(key, out object? v) || v == null) return fallback;
        if (v is bool b) return b;
        return fallback;
    }

    private sealed class Reader
    {
        private readonly string _s;
        private int _i;
        public Reader(string s) { _s = s; }

        public object? ParseValue()
        {
            Skip();
            if (_i >= _s.Length) return null;
            char c = _s[_i];
            if (c == '{') return ParseObject();
            if (c == '[') return ParseArray();
            if (c == '"') return ParseString();
            if (c == 't' || c == 'f') return ParseLiteral();
            if (c == 'n') { Expect("null"); return null; }
            return ParseNumber();
        }

        private Dictionary<string, object?> ParseObject()
        {
            _i++;
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                Skip();
                if (_i < _s.Length && _s[_i] == '}') { _i++; break; }
                string key = ParseString();
                Skip();
                if (_i >= _s.Length || _s[_i] != ':') throw new FormatException("JSON object is missing ':'.");
                _i++;
                obj[key] = ParseValue();
                Skip();
                if (_i < _s.Length && _s[_i] == ',') { _i++; continue; }
                if (_i < _s.Length && _s[_i] == '}') { _i++; break; }
                throw new FormatException("JSON object is malformed.");
            }
            return obj;
        }

        private List<object?> ParseArray()
        {
            _i++;
            var list = new List<object?>();
            while (true)
            {
                Skip();
                if (_i < _s.Length && _s[_i] == ']') { _i++; break; }
                list.Add(ParseValue());
                Skip();
                if (_i < _s.Length && _s[_i] == ',') { _i++; continue; }
                if (_i < _s.Length && _s[_i] == ']') { _i++; break; }
                throw new FormatException("JSON array is malformed.");
            }
            return list;
        }

        private string ParseString()
        {
            if (_s[_i] != '"') throw new FormatException("JSON string is missing an opening quote.");
            _i++;
            var sb = new StringBuilder();
            while (_i < _s.Length)
            {
                char c = _s[_i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (_i >= _s.Length) break;
                char e = _s[_i++];
                sb.Append(e switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'u' when _i + 4 <= _s.Length => (char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    _ => e
                });
                if (e == 'u') _i += 4;
            }
            throw new FormatException("JSON string is not terminated.");
        }

        private object ParseNumber()
        {
            int start = _i;
            if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            if (_i < _s.Length && _s[_i] == '.')
            {
                _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            }
            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                _i++;
                if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            }
            string n = _s.Substring(start, _i - start);
            if (n.IndexOf('.') < 0 && n.IndexOf('e') < 0 && n.IndexOf('E') < 0
                && long.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)
                && l <= int.MaxValue && l >= int.MinValue)
                return (int)l;
            if (long.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out long big))
                return big;
            return double.Parse(n, CultureInfo.InvariantCulture);
        }

        private object ParseLiteral()
        {
            if (Starts("true")) { _i += 4; return true; }
            if (Starts("false")) { _i += 5; return false; }
            throw new FormatException("JSON literal is invalid.");
        }

        private void Expect(string token)
        {
            if (!Starts(token)) throw new FormatException("Expected " + token + ".");
            _i += token.Length;
        }

        private bool Starts(string token) => _i + token.Length <= _s.Length && string.CompareOrdinal(_s, _i, token, 0, token.Length) == 0;

        private void Skip()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }
    }
}
