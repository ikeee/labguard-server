using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LabGuardServer.Core.Util;

namespace LabGuardServer.Core.Catalog
{
    /// <summary>字段种类（与客户端 FieldKind 同名同义）。</summary>
    public enum CatalogFieldKind { Bool, Choice, Number, Text, Path, List }

    /// <summary>一个配置项（由客户端 catalog.json 快照承载，服务端不手写任何一项）。</summary>
    public sealed class CatalogField
    {
        public string Section;
        public string Path;
        public string Label;
        public string Hint;
        public CatalogFieldKind Kind;
        public string[] Choices;
        public string[] Values;
        public decimal Min;
        public decimal Max;
        public bool Dangerous;
        public object Default;
    }

    /// <summary>
    /// 开关目录契约快照（labguard-catalog/1）。由客户端 `LabGuard.Settings.exe --export-catalog` 导出，
    /// 本类只做加载与完整性校验；渲染由 UI 层按 Kind 动态生成控件。
    /// </summary>
    public sealed class CatalogDoc
    {
        public const string ExpectedFormat = "labguard-catalog/1";

        public string ClientVersion;
        public string GeneratedAt;
        public List<CatalogField> Fields = new List<CatalogField>();
        /// <summary>默认策略（new GuardConfig() 序列化）明文 JSON——首次保存策略时的底稿。</summary>
        public string DefaultPolicyJson;

        public static CatalogDoc Load(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("开关目录快照不存在：" + path, path);
            var raw = Json.ParseObject(File.ReadAllText(path, Encoding.UTF8));
            string format = raw.TryGetValue("format", out object f) ? f as string : null;
            if (format != ExpectedFormat)
                throw new InvalidDataException("开关目录格式不认识（期望 " + ExpectedFormat + "，实际 " + format + "）——客户端与服务端版本不匹配");
            var doc = new CatalogDoc
            {
                ClientVersion = raw.TryGetValue("clientVersion", out object v) ? v as string : "?",
                GeneratedAt = raw.TryGetValue("generatedAt", out object g) ? g as string : "?",
                DefaultPolicyJson = raw.TryGetValue("defaultPolicy", out object dp) ? dp as string : null,
            };
            if (string.IsNullOrEmpty(doc.DefaultPolicyJson))
                throw new InvalidDataException("开关目录缺少 defaultPolicy");
            // 注意：JavaScriptSerializer 把 JSON 数组反序列化成 ArrayList（不是 object[]）
            if (!(raw.TryGetValue("fields", out object fieldsObj) && fieldsObj is System.Collections.IEnumerable fields))
                throw new InvalidDataException("开关目录缺少 fields（实际键：" +
                    string.Join(",", raw.Keys.ToArray()) + "）");
            foreach (object o in fields)
            {
                var m = o as Dictionary<string, object>;
                if (m == null) continue;
                var field = new CatalogField
                {
                    Section = m.TryGetValue("section", out object s) ? s as string : "",
                    Path = m.TryGetValue("path", out object p) ? p as string : "",
                    Label = m.TryGetValue("label", out object l) ? l as string : "",
                    Hint = m.TryGetValue("hint", out object h) ? h as string : "",
                    Dangerous = m.TryGetValue("dangerous", out object d) && d is bool b && b,
                    Default = m.TryGetValue("default", out object def) ? def : null,
                };
                if (string.IsNullOrEmpty(field.Path) || string.IsNullOrEmpty(field.Label))
                    throw new InvalidDataException("开关目录存在缺 path/label 的字段");
                if (!Enum.TryParse(m.TryGetValue("kind", out object k) ? k as string : "", out CatalogFieldKind kind))
                    throw new InvalidDataException("开关目录字段 kind 不认识：" + field.Path);
                field.Kind = kind;
                field.Choices = ToStringArray(m.TryGetValue("choices", out object c) ? c : null);
                field.Values = ToStringArray(m.TryGetValue("values", out object val) ? val : null);
                if (field.Kind == CatalogFieldKind.Choice &&
                    (field.Choices == null || field.Values == null || field.Choices.Length != field.Values.Length))
                    throw new InvalidDataException("Choice 字段缺 choices/values 或两者长度不一致：" + field.Path);
                field.Min = m.TryGetValue("min", out object min) && min is IConvertible ? Convert.ToDecimal(min) : 0;
                field.Max = m.TryGetValue("max", out object max) && max is IConvertible ? Convert.ToDecimal(max) : 100000;
                doc.Fields.Add(field);
            }
            if (doc.Fields.Count == 0) throw new InvalidDataException("开关目录是空的");
            return doc;
        }

        /// <summary>完整性自检：每个字段的 path 都能在默认策略字典里解析到值。</summary>
        public List<string> ValidateAgainstDefaultPolicy()
        {
            var errors = new List<string>();
            var defaults = Json.ParseObject(DefaultPolicyJson);
            foreach (CatalogField f in Fields)
            {
                object val = PathAccess.Get(defaults, f.Path);
                if (val == null) errors.Add("默认策略里找不到 " + f.Path);
            }
            return errors;
        }

        public List<string> Sections() => Fields.Select(f => f.Section).Distinct().ToList();

        /// <summary>ArrayList / object[] / null → string[]（JavaScriptSerializer 的数组是 ArrayList）。</summary>
        private static string[] ToStringArray(object o)
        {
            if (o is System.Collections.IEnumerable en && !(o is string))
            {
                return en.Cast<object>().Select(x => x as string ?? "").ToArray();
            }
            return null;
        }
    }
}
