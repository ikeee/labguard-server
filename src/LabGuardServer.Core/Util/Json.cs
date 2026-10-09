using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace LabGuardServer.Core.Util
{
    /// <summary>
    /// JSON 工具：统一走 System.Web.Extensions 的 JavaScriptSerializer——
    /// 与客户端 ConfigStore 完全同一序列化器，保证 policyJson 两边逐字节兼容。
    /// </summary>
    public static class Json
    {
        private static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };

        public static string Stringify(object obj) => Ser.Serialize(obj);

        /// <summary>解析为嵌套字典（数字→int/double，数组→object[]，对象→Dictionary&lt;string,object&gt;）。</summary>
        public static Dictionary<string, object> ParseObject(string json) => Ser.Deserialize<Dictionary<string, object>>(json);

        public static T Parse<T>(string json) => Ser.Deserialize<T>(json);
    }

    /// <summary>嵌套字典的点路径访问：与 SettingsCatalog 的 Path（如 "Classroom.MainExecutable"）一一对应。</summary>
    public static class PathAccess
    {
        /// <summary>取值；路径不存在返回 null。</summary>
        public static object Get(Dictionary<string, object> root, string dottedPath)
        {
            object node = root;
            foreach (string part in dottedPath.Split('.'))
            {
                var dict = node as Dictionary<string, object>;
                if (dict == null || !dict.TryGetValue(part, out node)) return null;
            }
            return node;
        }

        /// <summary>设值；中间层级不存在时自动创建。返回 false = 路径非法定义（如中间节点是数组）。</summary>
        public static bool Set(Dictionary<string, object> root, string dottedPath, object value)
        {
            if (string.IsNullOrEmpty(dottedPath)) return false;
            string[] parts = dottedPath.Split('.');
            object node = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var dict = node as Dictionary<string, object>;
                if (dict == null) return false;
                if (!dict.TryGetValue(parts[i], out object next) || !(next is Dictionary<string, object>))
                {
                    if (next != null) return false;   // 中间节点是叶子/数组，路径定义有问题
                    next = new Dictionary<string, object>();
                    dict[parts[i]] = next;
                }
                node = next;
            }
            var leaf = node as Dictionary<string, object>;
            if (leaf == null) return false;
            leaf[parts[parts.Length - 1]] = value;
            return true;
        }
    }
}
