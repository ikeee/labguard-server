using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LabGuardServer.Core.Policy;
using LabGuardServer.Core.Util;

namespace LabGuardServer.Core.Api
{
    /// <summary>
    /// 教师机 HTTP API（docs/protocol-v1.md）：GET /api/version、GET /api/policy、POST /api/checkin。
    /// HttpListener 自托管；处理逻辑全部抽成可单测的纯方法（SelfTest 不依赖真实端口也能断言）。
    /// </summary>
    public sealed class ApiServer : IDisposable
    {
        public const int DefaultPort = 8210;

        private readonly PolicyStore _store;
        private readonly OnlineList _online;
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        /// <summary>checkin 限速：同 IP 每秒最多 N 次（防伪造心跳淹没）。</summary>
        private const int CheckinPerSecondPerIp = 5;
        private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> _checkinTimes =
            new ConcurrentDictionary<string, ConcurrentQueue<DateTime>>(StringComparer.Ordinal);

        /// <summary>实际监听成功的 prefix（含端口），UI 展示 / 配对文件导出用。</summary>
        public string BoundPrefix { get; private set; }

        public OnlineList Online => _online;

        public ApiServer(PolicyStore store, OnlineList online)
        {
            _store = store;
            _online = online;
        }

        /// <summary>依次尝试候选 prefix（+:8210 需管理员/URLACL；失败退回 localhost）。</summary>
        public bool Start(int port, string hostname = null)
        {
            string host = string.IsNullOrEmpty(hostname) ? "+" : hostname;
            string[] candidates = host == "+"
                ? new[] { "http://+:" + port + "/", "http://localhost:" + port + "/", "http://127.0.0.1:" + port + "/" }
                : new[] { "http://" + host + ":" + port + "/" };
            foreach (string prefix in candidates)
            {
                try
                {
                    var l = new HttpListener();
                    l.Prefixes.Add(prefix);
                    l.Start();
                    _listener = l;
                    BoundPrefix = prefix;
                    _running = true;
                    _thread = new Thread(Loop) { IsBackground = true, Name = "LabGuardServer.Api" };
                    _thread.Start();
                    return true;
                }
                catch (HttpListenerException)
                {
                    // 该 prefix 被拒（权限/占用），试下一个
                }
            }
            return false;
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            BoundPrefix = null;
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception) { if (_running) Thread.Sleep(200); continue; }
                // 红队 A6-2 修复：每连接交给线程池处理，单个慢客户端不再阻塞全部请求
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Handle(ctx); }
                    catch (Exception) { TrySafe(ctx, 500, "text/plain", "internal error"); }
                });
            }
        }

        /// <summary>请求体上限（字节）。checkin 心跳远用不了这么大；超过直接 413 不读流。</summary>
        private const int MaxBodyBytes = 8192;

        /// <summary>读请求体的整体时限。慢客户端（发了 Content-Length 不发 body）到点断开。</summary>
        private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(5);

        private void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            string method = ctx.Request.HttpMethod;
            string ip = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "?";
            // GET 端点同时接受 HEAD（语义等同 GET 但不写 body，TrySafe 已做 HEAD 抑制）
            bool isGet = method == "GET" || method == "HEAD";

            if (path == "/api/version" && isGet)
            {
                Respond(ctx, GetVersion(_store));
                return;
            }
            if (path == "/api/policy" && isGet)
            {
                Respond(ctx, GetPolicy(_store));
                return;
            }
            if (path == "/api/checkin" && method == "POST")
            {
                // 红队 A5/A6-2 修复：Content-Length 先验，超大 body 直接拒收不进读流；
                // 读 body 限时，慢客户端到点 408 并断连
                long declared = ctx.Request.ContentLength64;
                if (declared > MaxBodyBytes)
                { TrySafe(ctx, 413, "text/plain", "payload too large"); return; }
                string body = ReadBodyWithTimeout(ctx);
                if (body == null)
                {
                    TrySafe(ctx, 408, "text/plain", "request body timeout");
                    try { ctx.Response.Abort(); } catch { }
                    return;
                }
                if (body.Length > MaxBodyBytes) { TrySafe(ctx, 413, "text/plain", "payload too large"); return; }
                Respond(ctx, Checkin(body, ip, _online, _checkinTimes, DateTime.UtcNow));
                return;
            }
            TrySafe(ctx, 404, "text/plain", "not found");
        }

        /// <summary>限时读请求体。超时返回 null（连接由调用方 Abort）；任何时刻超过上限立即停读。</summary>
        private static string ReadBodyWithTimeout(HttpListenerContext ctx)
        {
            Stream stream = ctx.Request.InputStream;
            byte[] buf = new byte[MaxBodyBytes + 1];
            int total = 0;
            long deadline = DateTime.UtcNow.Ticks + BodyReadTimeout.Ticks;
            while (total < buf.Length)
            {
                long remainingMs = (deadline - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond;
                if (remainingMs <= 0) return null;
                Task<int> read = Task.Factory.FromAsync(
                    (cb, s) => stream.BeginRead(buf, total, buf.Length - total, cb, s), stream.EndRead, null);
                if (!read.Wait((int)Math.Min(remainingMs, int.MaxValue)))
                    return null;
                int n = read.Result;
                if (n <= 0) break;
                total += n;
            }
            Encoding enc = ctx.Request.ContentEncoding ?? Encoding.UTF8;
            return enc.GetString(buf, 0, total);
        }

        private static void Respond(HttpListenerContext ctx, ApiReply reply)
        {
            TrySafe(ctx, reply.Status, reply.ContentType, reply.Body);
        }

        private static void TrySafe(HttpListenerContext ctx, int status, string contentType, string body)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body ?? "");
                // 红队 A6-3 修复：HEAD 请求写 body 会被 HttpListener 拒绝（抛异常→连接不关闭→挂死），
                // HEAD 只设 Content-Length 不写流
                bool isHead = string.Equals(ctx.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = contentType;
                ctx.Response.ContentLength64 = bytes.Length;
                if (!isHead)
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch
            {
                // 兜底：任何失败都要 Abort 连接，绝不留挂死连接
                try { ctx.Response.Abort(); } catch { }
            }
        }

        // ---------------------------------------------------------------- 可单测的纯逻辑

        public sealed class ApiReply
        {
            public int Status;
            public string ContentType;
            public string Body;
        }

        /// <summary>GET /api/version → {"seq":N,"sha256":"…","paused":false}；未配置策略 → 503。</summary>
        public static ApiReply GetVersion(PolicyStore store)
        {
            PolicyVersion v = store?.Version();
            if (v == null) return new ApiReply { Status = 503, ContentType = "text/plain", Body = "policy not configured" };
            return new ApiReply
            {
                Status = 200,
                ContentType = "application/json",
                Body = Json.Stringify(new Dictionary<string, object>
                {
                    { "seq", v.Seq },
                    { "sha256", v.Sha256Hex },
                    { "paused", v.Paused },
                }),
            };
        }

        /// <summary>GET /api/policy → LGSRV1 信封全文（UTF-8，显式 charset 防客户端按本地代码页解码）；未配置策略 → 503。</summary>
        public static ApiReply GetPolicy(PolicyStore store)
        {
            if (store?.CurrentEnvelope == null)
                return new ApiReply { Status = 503, ContentType = "text/plain", Body = "policy not configured" };
            return new ApiReply { Status = 200, ContentType = "text/plain; charset=utf-8", Body = store.CurrentEnvelope };
        }

        /// <summary>POST /api/checkin：解析心跳、限速、聚合在线列表。字段类型严格校验（红队 A1-9：类型错乱必须 400 不能 500）。</summary>
        public static ApiReply Checkin(string body, string ip, OnlineList online,
            ConcurrentDictionary<string, ConcurrentQueue<DateTime>> rateMap, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(body) || body.Length > 8192)
                return new ApiReply { Status = 400, ContentType = "text/plain", Body = "bad request" };

            ConcurrentQueue<DateTime> times = rateMap.GetOrAdd(ip, _ => new ConcurrentQueue<DateTime>());
            lock (times)
            {
                while (times.TryPeek(out DateTime t) && (nowUtc - t).TotalSeconds >= 1) times.TryDequeue(out _);
                if (times.Count >= CheckinPerSecondPerIp)
                    return new ApiReply { Status = 429, ContentType = "text/plain", Body = "too many requests" };
                times.Enqueue(nowUtc);
            }
            // 限速表防泄漏：条目过多时清掉 60 秒没动静的 IP（洪泛换 IP 也撑不爆内存）
            if (rateMap.Count > 1024)
            {
                foreach (var kv in rateMap)
                {
                    if (kv.Value.TryPeek(out DateTime t) && (nowUtc - t).TotalSeconds > 60)
                        rateMap.TryRemove(kv.Key, out _);
                }
            }

            Dictionary<string, object> data;
            try { data = Json.ParseObject(body); }
            catch { return new ApiReply { Status = 400, ContentType = "text/plain", Body = "bad json" }; }

            if (!data.TryGetValue("machine", out object m) || !(m is string machine) ||
                machine.Length == 0 || machine.Length > 256)
                return new ApiReply { Status = 400, ContentType = "text/plain", Body = "missing or invalid machine" };

            string version = "?";
            if (data.TryGetValue("version", out object ver) && ver != null)
            {
                if (!(ver is string vs) || vs.Length > 32)
                    return new ApiReply { Status = 400, ContentType = "text/plain", Body = "invalid version" };
                version = vs;
            }

            ulong seq = 0;
            if (data.TryGetValue("seq", out object s) && s != null)
            {
                // 只认整数类型；字符串/负数/溢出一律 400（Convert.ToUInt64 遇 "abc"/-1/溢出会抛异常变 500）
                if (s is ulong u) seq = u;
                else if (s is long l) { if (l < 0) return new ApiReply { Status = 400, ContentType = "text/plain", Body = "invalid seq" }; seq = (ulong)l; }
                else if (s is int i) { if (i < 0) return new ApiReply { Status = 400, ContentType = "text/plain", Body = "invalid seq" }; seq = (uint)i; }
                else if (s is double db && db == Math.Floor(db) && db >= 0 && db <= ulong.MaxValue) seq = Convert.ToUInt64(db);
                else return new ApiReply { Status = 400, ContentType = "text/plain", Body = "invalid seq" };
            }

            bool paused = data.TryGetValue("paused", out object p) && p is bool pb && pb;

            online.Record(machine, version, seq, paused, ip);
            return new ApiReply { Status = 200, ContentType = "application/json", Body = "{\"ok\":true}" };
        }

        public void Dispose() => Stop();
    }
}
