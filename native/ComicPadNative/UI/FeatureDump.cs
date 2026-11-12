// 기능 지도 재료 뽑기 (창 없이): ComicPadNative.exe --dump-features 결과.json
//  - 등록된 메뉴·도구·패널마다 "등록한 곳"(호출 스택)과 "실제 동작 코드"(동작 함수의 PDB 줄)를 적는다.
//  - 람다(() => X.Y())면 IL 을 읽어 안에서 부르는 우리 함수(마지막 호출 / 함수 넘기기)를 따라간다.
//  - 웹 판 메뉴 표(WebMenus)의 항목이 어느 네이티브 메뉴로 이어지는지도 같이.
// tools/feature_map.py 가 이 JSON + 웹 판 코드를 합쳐 native/기능_코드_지도.html 을 만든다.
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ComicPadNative.UI;

public static class FeatureDump
{
    static MetadataReader pdb;
    static readonly Assembly Asm = typeof(FeatureDump).Assembly;

    public static void Run(string outPath)
    {
        try
        {
            var pdbPath = Path.ChangeExtension(Asm.Location, ".pdb");
            if (File.Exists(pdbPath)) pdb = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdbPath)).GetMetadataReader();
        }
        catch { pdb = null; }

        var root = new JsonObject();
        var menus = new JsonArray();
        foreach (var m in Registry.Menus.OrderBy(m => m.Path, StringComparer.Ordinal))
        {
            if (m.Path.Split('/')[^1].StartsWith("-")) continue;
            menus.Add(new JsonObject
            {
                ["path"] = m.Path,
                ["key"] = m.Shortcut == Keys.None ? null : new KeysConverter().ConvertToString(m.Shortcut),
                ["reg"] = Reg("menu:" + m.Path),
                ["impl"] = Impl(m.Run?.Method),
                ["inner"] = Inner(m.Run),
            });
        }
        root["menus"] = menus;
        var tools = new JsonArray();
        foreach (var t in Registry.Tools)
        {
            var type = t.GetType();
            var main = type.GetMethod("Down", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                       ?? type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).FirstOrDefault(x => !x.IsSpecialName)
                       ?? (MethodBase)type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault();
            tools.Add(new JsonObject { ["id"] = t.Id, ["name"] = t.Name, ["type"] = type.FullName, ["reg"] = Reg("tool:" + t.Id), ["impl"] = Loc(main) });
        }
        root["tools"] = tools;
        var panels = new JsonArray();
        foreach (var p in Registry.Panels) panels.Add(new JsonObject { ["title"] = p.Title, ["dock"] = p.Dock, ["reg"] = Reg("panel:" + p.Title), ["impl"] = Impl(p.Create?.Method) });
        root["panels"] = panels;
        // 웹 판 메뉴 표 → 네이티브 경로
        var web = new JsonArray();
        try
        {
            foreach (var top in Chrome.WebMenus.Tree())
            {
                var items = new JsonArray();
                foreach (var n in top.Items) Add(items, n);
                web.Add(new JsonObject { ["id"] = top.Id, ["label"] = top.Label, ["items"] = items });
            }
        }
        catch (Exception ex) { root["webError"] = ex.Message; }
        root["webMenus"] = web;
        File.WriteAllText(outPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        static void Add(JsonArray into, Chrome.MenuNode n)
        {
            if (n.Sep) return;
            var o = new JsonObject { ["key"] = n.Key, ["label"] = n.Label, ["path"] = n.Path, ["native"] = n.Native };
            if (n.Sub != null) { var sub = new JsonArray(); foreach (var c in n.Sub) Add(sub, c); o["sub"] = sub; }
            into.Add(o);
        }
    }

    /// <summary>등록한 곳: 호출 스택에서 도우미(지역 함수·짧은 이름 도우미)를 건너뛴 첫 줄 + 바로 앞 줄</summary>
    static JsonNode Reg(string key)
    {
        if (!Registry.Src.TryGetValue(key, out var st)) return null;
        var frames = st.GetFrames().Where(f => f.GetFileName() != null && f.GetMethod()?.DeclaringType?.Assembly == Asm).ToList();
        if (frames.Count == 0) return null;
        int i = 0;
        while (i + 1 < frames.Count && IsHelper(frames[i].GetMethod())) i++;
        var f = frames[i];
        var o = new JsonObject { ["file"] = f.GetFileName(), ["line"] = f.GetFileLineNumber(), ["method"] = Name(f.GetMethod()) };
        if (i > 0) o["via"] = new JsonObject { ["file"] = frames[0].GetFileName(), ["line"] = frames[0].GetFileLineNumber(), ["method"] = Name(frames[0].GetMethod()) };
        return o;
    }

    static bool IsHelper(MethodBase m)
    {
        if (m == null) return false;
        string n = m.Name;
        if (n.Contains("g__")) return true;                          // 지역 함수 (void M(...) 안에서 Registry.Menu)
        if (n.Length <= 4 && n != "Init") return true;                // M, A, Reg, Add 같은 짧은 도우미
        if (n is "Menu" or "Item" or "Cmd" or "Register" or "RegisterMenus" or "Def") return true;
        return false;
    }

    /// <summary>다른 모듈이 메뉴를 감싸 다시 등록한 경우 (예: 내보내기 전 확인): 감싼 원래 동작</summary>
    static JsonNode Inner(Delegate d)
    {
        var target = d?.Target;
        if (target == null) return null;
        foreach (var f in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (f.GetValue(target) is Action inner && inner != d)
                return Impl(inner.Method);
        return null;
    }

    static string Name(MethodBase m) => m == null ? "" : (m.DeclaringType?.Name ?? "") + "." + m.Name;

    /// <summary>동작 함수의 실제 코드 줄. 람다·컴파일러가 만든 함수면 그 안에서 부르는 우리 함수를 따라감</summary>
    static JsonNode Impl(MethodInfo m, int depth = 0)
    {
        if (m == null) return null;
        bool generated = m.Name.StartsWith("<") || (m.DeclaringType?.Name.StartsWith("<") ?? false);
        if (generated && depth < 3)
        {
            var target = CalledMethod(m);
            if (target is MethodInfo mi && mi != m)
            {
                var inner = Impl(mi, depth + 1);
                if (inner != null) { if (inner is JsonObject io) io["lambda"] = Loc(m); return inner; }
            }
        }
        return Loc(m);
    }

    /// <summary>함수 몸통 IL 에서 우리 어셈블리의 함수 고르기: 함수 넘기기(ldftn) > 마지막 호출 (App·Registry·속성 접근 빼고)</summary>
    static MethodBase CalledMethod(MethodInfo m)
    {
        byte[] il;
        try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { return null; }
        if (il == null) return null;
        MethodBase ldftn = null, last = null;
        for (int i = 0; i < il.Length; i++)
        {
            int op = il[i]; bool ftn = false;
            if (op == 0xFE && i + 1 < il.Length && (il[i + 1] == 0x06 || il[i + 1] == 0x07)) { ftn = true; i++; }
            else if (op != 0x28 && op != 0x6F && op != 0x73) continue;          // call, callvirt, newobj
            if (i + 4 >= il.Length) break;
            int token = BitConverter.ToInt32(il, i + 1);
            MethodBase target = null;
            try { target = m.Module.ResolveMethod(token, m.DeclaringType?.GetGenericArguments(), m.GetGenericArguments()); } catch { }
            if (target == null || target.DeclaringType?.Assembly != Asm) continue;
            string tn = target.DeclaringType?.Name ?? "";
            if (tn is "App" or "Registry" || target.IsConstructor || target.Name.StartsWith("get_") || target.Name.StartsWith("set_") || target.Name.StartsWith("op_")) continue;
            if (target.Name == "Run" && tn is "MainForm" or "ComicModule") continue;
            if (ftn) ldftn ??= target; else last = target;
        }
        return ldftn ?? last;
    }

    static JsonNode Loc(MethodBase m)
    {
        if (m == null || pdb == null || m.DeclaringType?.Assembly != Asm) return null;
        try
        {
            var h = (MethodDefinitionHandle)MetadataTokens.EntityHandle(m.MetadataToken);
            var info = pdb.GetMethodDebugInformation(h.ToDebugInformationHandle());
            foreach (var sp in info.GetSequencePoints())
            {
                if (sp.IsHidden) continue;
                var doc = pdb.GetDocument(sp.Document);
                return new JsonObject { ["file"] = pdb.GetString(doc.Name), ["line"] = sp.StartLine, ["method"] = Name(m) };
            }
        }
        catch { }
        return null;
    }
}
