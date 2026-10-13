using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using LabGuardServer.Core.Api;
using LabGuardServer.Core.Catalog;
using LabGuardServer.Core.Envelope;
using LabGuardServer.Core.Policy;
using LabGuardServer.Core.Util;

namespace LabGuardServer.SelfTest
{
    /// <summary>
    /// 服务端自检：全程干跑（临时目录 + 本机回环端口），不动 ProgramData、不改系统。
    /// 与客户端 labguard 的自检纪律对齐：退出码 0 = 全过。
    /// </summary>
    internal static class Program
    {
        private static int _pass;
        private static int _fail;

        private static void Check(bool cond, string name, string detail = null)
        {
            if (cond) { _pass++; Console.WriteLine("  PASS  " + name); }
            else { _fail++; Console.WriteLine("  FAIL  " + name + (detail == null ? "" : "  —— " + detail)); }
        }

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            string tmp = Path.Combine(Path.GetTempPath(), "labguard-server-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try
            {
                RunAll(tmp);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine("结果：" + _pass + " 通过，" + _fail + " 失败" + (_fail == 0 ? " ✅" : " ❌"));
            return _fail == 0 ? 0 : 1;
        }

        private static void RunAll(string tmp)
        {
            // ================================================================ [1] 信封
            Console.WriteLine("[1] LGSRV1 信封");
            using (var keys = RsaKeyStore.LoadOrCreate(tmp))
            using (var evilKeys = RsaKeyStore.LoadOrCreate(Path.Combine(tmp, "evil")))
            {
                string policyJson = Json.Stringify(Json.ParseObject("{\"Enabled\":true,\"Classroom\":{\"Enabled\":true,\"ProcessNames\":[\"a|b\"]}}"));
                string env = PolicyEnvelope.Build(7, DateTime.UtcNow, false, policyJson, keys.Rsa);

                var d = PolicyEnvelope.Parse(env, keys.Rsa);
                Check(d.Verified, "1.1 组装→解析→验签 通过");
                Check(d.Seq == 7 && !d.Paused, "1.2 seq/paused 字段正确");
                Check(d.PolicyJson == policyJson, "1.3 policyJson 原文一致（含竖线字符不丢）");

                var d2 = PolicyEnvelope.Parse(env, evilKeys.Rsa);
                Check(!d2.Verified, "1.4 用错误的公钥验签失败（伪服务器/篡改检出）");

                string tampered = env.Replace("\"Enabled\":true", "\"Enabled\":false");
                Check(!PolicyEnvelope.Parse(tampered, keys.Rsa).Verified, "1.5 篡改 policyJson 验签失败");

                string pausedFlip = env.Replace("|0|", "|1|");
                Check(!PolicyEnvelope.Parse(pausedFlip, keys.Rsa).Verified, "1.6 篡改 paused 位验签失败（伪造全员暂停检出）");

                Check(!PolicyEnvelope.Parse("LGSRV2|" + env.Substring(7), keys.Rsa).Verified, "1.7 不认识的魔数整封拒绝");
                Check(!PolicyEnvelope.Parse("garbage", keys.Rsa).Verified, "1.8 非法输入安全拒绝");
                Check(!PolicyEnvelope.Parse(null, keys.Rsa).Verified, "1.9 null 输入安全拒绝");

                string envPaused = PolicyEnvelope.Build(8, DateTime.UtcNow, true, policyJson, keys.Rsa);
                var d3 = PolicyEnvelope.Parse(envPaused, keys.Rsa);
                Check(d3.Verified && d3.Paused, "1.10 paused=1 信封往返正确");

                Check(PolicyEnvelope.IsNewer(8, 7), "1.11 防回滚：seq 严格更大才算新");
                Check(!PolicyEnvelope.IsNewer(7, 7) && !PolicyEnvelope.IsNewer(6, 7), "1.12 防回滚：等于/更小拒绝（重放/回滚）");

                // ================================================================ [2] 密钥库
                Console.WriteLine("[2] 密钥库");
                Check(File.Exists(Path.Combine(tmp, "server-key.bin")), "2.1 密钥落盘");
                Check(!File.ReadAllText(Path.Combine(tmp, "server-key.bin"), Encoding.UTF8).Contains("<RSAKeyValue>"),
                    "2.2 密钥文件不是明文 XML（DPAPI 加密）");
                using (var reloaded = RsaKeyStore.LoadOrCreate(tmp))
                {
                    Check(reloaded.PublicXml == keys.PublicXml, "2.3 重新加载得到同一密钥");
                    Check(!string.IsNullOrEmpty(keys.Fingerprint) && reloaded.Fingerprint == keys.Fingerprint, "2.4 指纹稳定");
                }

                // ================================================================ [3] 开关目录
                Console.WriteLine("[3] 开关目录契约快照");
                string catalogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "catalog.json");
                CatalogDoc catalog = null;
                try
                {
                    catalog = CatalogDoc.Load(catalogPath);
                    Check(true, "3.1 catalog.json 加载");
                }
                catch (Exception ex)
                {
                    Check(false, "3.1 catalog.json 加载", ex.Message);
                }
                if (catalog != null)
                {
                    Check(catalog.Fields.Count >= 100, "3.2 字段齐全（≥100 项，当前 " + catalog.Fields.Count + "）");
                    Check(catalog.Sections().Count >= 12, "3.3 分组齐全（≥12 组，当前 " + catalog.Sections().Count + "）");
                    List<string> errors = catalog.ValidateAgainstDefaultPolicy();
                    Check(errors.Count == 0, "3.4 每个字段 path 都能在默认策略里解析", string.Join("; ", errors.Take(3)));
                    Check(catalog.Fields.All(f => f.Kind != CatalogFieldKind.Choice || (f.Choices?.Length ?? 0) > 0),
                        "3.5 Choice 字段都有选项");
                    var dup = catalog.Fields.GroupBy(f => f.Path).FirstOrDefault(g => g.Count() > 1);
                    Check(dup == null, "3.6 无重复 path", dup?.Key);
                    Check(catalog.DefaultPolicyJson.Contains("\"Enabled\":true"), "3.7 默认策略 JSON 可读");
                }

                // ================================================================ [4] 策略仓库
                Console.WriteLine("[4] 策略仓库");
                string storeDir = Path.Combine(tmp, "store");
                var store = new PolicyStore(storeDir, keys);
                store.Load();
                Check(store.CurrentEnvelope == null, "4.1 初始无策略");
                Check(ApiServer.GetVersion(store).Status == 503, "4.2 未配置策略 → /api/version 503");
                Check(ApiServer.GetPolicy(store).Status == 503, "4.3 未配置策略 → /api/policy 503");

                var policy = catalog != null ? Json.ParseObject(catalog.DefaultPolicyJson) : new Dictionary<string, object>();
                PathAccess.Set(policy, "StartDelaySeconds", 180);
                PolicyVersion v1 = store.Save(policy, false, catalog?.DefaultPolicyJson);
                Check(v1 != null && v1.Seq == 1, "4.4 首次保存 seq=1");

                var store2 = new PolicyStore(storeDir, keys);
                store2.Load();
                Check(store2.CurrentEnvelope == store.CurrentEnvelope, "4.5 落盘→重载 信封一致");
                var ver = ApiServer.GetVersion(store2);
                Check(ver.Status == 200 && ver.Body.Contains("\"seq\":1"), "4.6 /api/version 200 且 seq=1");

                string envelopeText = File.ReadAllText(Path.Combine(storeDir, "policy.envelope"), Encoding.UTF8);
                string tamperedEnvelope = envelopeText.Replace("\"StartDelaySeconds\":180", "\"StartDelaySeconds\":0");
                File.WriteAllText(Path.Combine(storeDir, "policy.envelope"), tamperedEnvelope);
                bool tamperCaught = false;
                try
                {
                    var store3 = new PolicyStore(storeDir, keys);
                    store3.Load();
                }
                catch (InvalidDataException)
                {
                    tamperCaught = true;
                }
                Check(tamperCaught, "4.7 磁盘信封被篡改 → 加载时验签拒绝（fail-closed）");
                File.WriteAllText(Path.Combine(storeDir, "policy.envelope"), envelopeText);

                // paused 翻转
                store2.Load();
                store2.SetPaused(true);
                Check(store2.Version().Paused && store2.Version().Seq == 2, "4.8 全员暂停 seq+1 且 paused=true");
                store2.SetPaused(false);
                Check(!store2.Version().Paused && store2.Version().Seq == 3, "4.9 全员恢复 seq+1 且 paused=false");
                var parsedBack = PolicyEnvelope.Parse(store2.CurrentEnvelope, keys.Rsa);
                object startDelay = PathAccess.Get(Json.ParseObject(parsedBack.PolicyJson), "StartDelaySeconds");
                Check(Convert.ToInt32(startDelay) == 180, "4.10 暂停/恢复不丢策略字段（StartDelaySeconds=180 仍在）");

                // 红队回归：磁盘换成签名合法的旧信封（seq 回退）→ Load(minSeq) 拒绝且内存现行信封不丢
                string goodEnvelope = store2.CurrentEnvelope;
                string oldEnvelope = PolicyEnvelope.Build(store2.CurrentSeq - 1, DateTime.UtcNow, false,
                    parsedBack.PolicyJson, keys.Rsa);
                File.WriteAllText(Path.Combine(storeDir, "policy.envelope"), oldEnvelope, new UTF8Encoding(false));
                bool rollbackCaught = false;
                try { store2.Load(store2.CurrentSeq); }
                catch (InvalidDataException) { rollbackCaught = true; }
                Check(rollbackCaught, "4.11 签名合法但 seq 回退的磁盘信封 → Load(minSeq) 拒绝");
                Check(store2.CurrentEnvelope == goodEnvelope && store2.Version().Seq == 3,
                    "4.12 拒绝后内存现行信封保留（服务不打回 503）");
                File.WriteAllText(Path.Combine(storeDir, "policy.envelope"), goodEnvelope, new UTF8Encoding(false));
                store2.Load(store2.CurrentSeq);
                Check(store2.Version().Seq == 3, "4.13 恢复正常信封后重载成功（同 seq 允许）");

                // ================================================================ [5] API 处理逻辑
                Console.WriteLine("[5] API 处理逻辑");
                var online = new OnlineList();
                var rateMap = new ConcurrentDictionary<string, ConcurrentQueue<DateTime>>();
                DateTime now = DateTime.UtcNow;

                ApiServer.ApiReply ok = ApiServer.Checkin("{\"machine\":\"PC-01\",\"version\":\"0.10\",\"seq\":1,\"paused\":false}", "192.168.1.11", online, rateMap, now);
                Check(ok.Status == 200, "5.1 checkin 正常心跳 200");
                Check(online.Snapshot().Length == 1 && online.Snapshot()[0].Machine == "PC-01", "5.2 在线列表聚合正确");

                ApiServer.ApiReply bad = ApiServer.Checkin("{not json", "192.168.1.11", online, rateMap, now);
                Check(bad.Status == 400, "5.3 坏 JSON → 400");
                ApiServer.ApiReply missing = ApiServer.Checkin("{\"version\":\"0.10\"}", "192.168.1.11", online, rateMap, now);
                Check(missing.Status == 400, "5.4 缺 machine → 400");
                ApiServer.ApiReply huge = ApiServer.Checkin(new string('x', 9000), "192.168.1.11", online, rateMap, now);
                Check(huge.Status == 400, "5.5 超长请求体 → 400");

                bool rateLimited = false;
                for (int i = 0; i < 8; i++)
                {
                    var r = ApiServer.Checkin("{\"machine\":\"FLOOD\"}", "10.0.0.9", online, rateMap, now);
                    if (r.Status == 429) { rateLimited = true; break; }
                }
                Check(rateLimited, "5.6 同 IP 高频心跳触发限速 429");

                var other = ApiServer.Checkin("{\"machine\":\"PC-02\"}", "192.168.1.12", online, rateMap, now);
                Check(other.Status == 200, "5.7 限速只针对同 IP（别的 IP 不受影响）");

                // 红队 A1-9 回归：字段类型错乱必须 400，不能 500
                Check(ApiServer.Checkin("{\"machine\":123}", "192.168.1.20", online, rateMap, now).Status == 400,
                    "5.9 machine 是数字 → 400");
                Check(ApiServer.Checkin("{\"machine\":\"PC\",\"seq\":\"5\"}", "192.168.1.20", online, rateMap, now).Status == 400,
                    "5.10 seq 是字符串 → 400");
                Check(ApiServer.Checkin("{\"machine\":\"PC\",\"seq\":-1}", "192.168.1.20", online, rateMap, now).Status == 400,
                    "5.11 seq 是负数 → 400");
                Check(ApiServer.Checkin("{\"machine\":\"PC\",\"version\":9}", "192.168.1.20", online, rateMap, now).Status == 400,
                    "5.12 version 是数字 → 400（version 只认字符串）");
                Check(ApiServer.Checkin("{\"machine\":\"PC\"}", "192.168.1.21", online, rateMap, now).Status == 200,
                    "5.13 缺 version/seq 仍 200（协议允许的可选字段，纯心跳）");

                // LG-003 协议对照补测：每条都写进 docs/protocol-conformance.md
                Check(ApiServer.Checkin("", "192.168.1.22", online, rateMap, now).Status == 400,
                    "5.14 空请求体 → 400");
                Check(ApiServer.Checkin("{\"machine\":\"PC-OK\",\"unknownField\":{\"a\":1}}", "192.168.1.23",
                        online, rateMap, now).Status == 200,
                    "5.15 未知字段被忽略（向前兼容）");
                Check(ApiServer.Checkin("{\"machine\":\"PC-D\",\"seq\":12.0}", "192.168.1.24",
                        online, rateMap, now).Status == 200,
                    "5.16 seq 为整数值的数字（12.0）→ 接受");
                Check(ApiServer.Checkin("{\"machine\":\"PC-F\",\"seq\":12.5}", "192.168.1.25",
                        online, rateMap, now).Status == 400,
                    "5.17 seq 有小数部分（12.5）→ 400");

                // 负超时 = 任何记录都判离线（确定性；正零会有同 tick 差值为 0 的分辨率问题）
                online.OfflineAfter = TimeSpan.FromSeconds(-5);
                Check(online.Snapshot().Length == 0, "5.8 超时判离线");
                online.OfflineAfter = TimeSpan.FromSeconds(60);

                // ================================================================ [6] 本机端口冒烟
                Console.WriteLine("[6] 本机端口冒烟（HttpListener 回环）");
                var smokeStore = new PolicyStore(Path.Combine(tmp, "smoke"), keys);
                smokeStore.Load();
                smokeStore.Save(policy, false, catalog?.DefaultPolicyJson);
                var smokeOnline = new OnlineList();
                using (var api = new ApiServer(smokeStore, smokeOnline))
                {
                    int port = FindFreePort();
                    bool started = api.Start(port, "127.0.0.1");
                    Check(started, "6.1 回环监听启动（127.0.0.1:" + port + "）");
                    if (started)
                    {
                        using (var wc = new WebClient { Encoding = Encoding.UTF8 })
                        {
                            string versionJson = wc.DownloadString("http://127.0.0.1:" + port + "/api/version");
                            Check(versionJson.Contains("\"seq\":1") && versionJson.Contains("\"paused\""), "6.2 GET /api/version 实测", versionJson);

                            string envelope2 = wc.DownloadString("http://127.0.0.1:" + port + "/api/policy");
                            var parsed = PolicyEnvelope.Parse(envelope2, keys.Rsa);
                            Check(parsed.Verified, "6.3 GET /api/policy 拉到的信封可验签");

                            wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                            string checkin = wc.UploadString("http://127.0.0.1:" + port + "/api/checkin",
                                "{\"machine\":\"SMOKE\",\"version\":\"0.10\",\"seq\":1,\"paused\":false}");
                            Check(checkin.Contains("\"ok\":true"), "6.4 POST /api/checkin 实测", checkin);
                            Check(smokeOnline.Snapshot().Any(r => r.Machine == "SMOKE"), "6.5 心跳进入在线列表");

                            try
                            {
                                wc.DownloadString("http://127.0.0.1:" + port + "/api/nope");
                                Check(false, "6.6 未知路径 404");
                            }
                            catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
                            {
                                Check(true, "6.6 未知路径 404");
                            }

                            // LG-003：未定义的方法一律 404（PUT 不是 checkin 的合法方法）
                            var put = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/checkin");
                            put.Method = "PUT";
                            put.ContentLength = 0;
                            put.Timeout = 5000;
                            try
                            {
                                put.GetResponse();
                                Check(false, "6.12 PUT /api/checkin → 404（未定义方法）");
                            }
                            catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
                            {
                                Check(true, "6.12 PUT /api/checkin → 404（未定义方法）");
                            }
                        }

                        // 红队 A6-3 回归：HEAD 不挂死（原版写 body 抛异常→连接不关→无限挂起）
                        var headReq = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/version");
                        headReq.Method = "HEAD";
                        headReq.Timeout = 5000;
                        using ((HttpWebResponse)headReq.GetResponse())
                            Check(true, "6.8 HEAD /api/version → 200 不挂死");
                        var head404 = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/nope");
                        head404.Method = "HEAD";
                        head404.Timeout = 5000;
                        try
                        {
                            head404.GetResponse();
                            Check(false, "6.9 HEAD 未知路径 404 不挂死");
                        }
                        catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
                        {
                            Check(true, "6.9 HEAD 未知路径 404 不挂死");
                        }

                        // 红队 A6-2 回归：慢 POST（声明 Content-Length 不发 body）不得阻塞其他请求
                        var slow = new System.Net.Sockets.TcpClient();
                        slow.Connect(IPAddress.Loopback, port);
                        var slowStream = slow.GetStream();
                        byte[] req = Encoding.ASCII.GetBytes(
                            "POST /api/checkin HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: 50\r\n\r\n");
                        slowStream.Write(req, 0, req.Length);
                        slowStream.Flush();
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        string during = new WebClient { Encoding = Encoding.UTF8 }
                            .DownloadString("http://127.0.0.1:" + port + "/api/version");
                        sw.Stop();
                        Check(during.Contains("\"seq\"") && sw.ElapsedMilliseconds < 3000,
                            "6.10 慢 POST 期间 /api/version 正常响应（" + sw.ElapsedMilliseconds + "ms）");
                        slow.Close();

                        // 红队 A5 回归：声明超大 Content-Length → 413（不进读流）
                        var big = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/checkin");
                        big.Method = "POST";
                        big.ContentType = "application/json";
                        big.Timeout = 8000;
                        byte[] bigPayload = new byte[8193];
                        bool big413 = false;
                        try
                        {
                            using (var rs = big.GetRequestStream()) rs.Write(bigPayload, 0, bigPayload.Length);
                            using (var br = (HttpWebResponse)big.GetResponse()) big413 = (int)br.StatusCode == 413;
                        }
                        catch (WebException ex)
                        {
                            big413 = (int)((ex.Response as HttpWebResponse)?.StatusCode ?? 0) == 413;
                        }
                        Check(big413, "6.11 超大请求体 → 413");
                    }
                    api.Stop();
                }
                Check(true, "6.7 Stop 后端口释放（无异常）");

                // ================================================================ [7] 策略检索与差异
                // 教师端"快速找到对应策略"的底座：搜索命中规则 + 发布前差异预览（都是纯函数，必须可测）。
                // 注：原 [7] 口令散列已随"策略面板不再要密码"（2026-10-12 产品决定）删除。
                Console.WriteLine("[7] 策略检索与差异预览");
                var fields = catalog.Fields;
                Check(PolicySearch.Matches(fields.First(f => f.Path == "Usb.AllowStorage"), "USB"),
                    "7.1 关键词命中分组名（USB → 6、USB存储设备）");
                Check(PolicySearch.Matches(fields.First(f => f.Path == "Network.DnsServers"), "dns"),
                    "7.2 大小写不敏感（dns → DNS 相关开关）");
                Check(PolicySearch.Filter(fields, "任务管理器").Count >= 1,
                    "7.3 命中中文提示里的词（任务管理器）",
                    PolicySearch.Filter(fields, "任务管理器").Count + " 项");
                Check(PolicySearch.Filter(fields, "   ").Count == fields.Count, "7.4 空关键词 = 全部（不筛选）");
                Check(PolicySearch.Filter(fields, "USB 存储").Count >= 1, "7.5 多个词 = AND（USB 存储）");
                Check(PolicySearch.Filter(fields, "这个词一定不存在").Count == 0, "7.6 无命中返回空");

                var oldPolicy = Json.ParseObject(catalog.DefaultPolicyJson);
                var newPolicy = Json.ParseObject(catalog.DefaultPolicyJson);
                Check(PolicyDiff.Compare(oldPolicy, newPolicy, fields).Count == 0, "7.7 两份相同策略 → 差异 0 条");
                PathAccess.Set(newPolicy, "Network.DnsServers", new[] { "10.28.1.10" });
                PathAccess.Set(newPolicy, "Classroom.Enabled", false);
                List<PolicyChange> changes = PolicyDiff.Compare(oldPolicy, newPolicy, fields);
                Check(changes.Count == 2, "7.8 改两项 → 差异 2 条", changes.Count + " 条");
                Check(changes.Any(c => c.Path == "Classroom.Enabled" && c.NewText == "关"),
                    "7.9 布尔值以「开/关」呈现给老师看");
                Check(changes.Any(c => c.Path == "Network.DnsServers" && c.NewText.Contains("10.28.1.10")),
                    "7.10 列表值以顿号连接呈现");
                Check(PolicyDiff.Format(null) == "（空）" && PolicyDiff.Format(true) == "开" &&
                      PolicyDiff.Format(false) == "关", "7.11 空值/布尔格式化");

                // ---- 职责归属（2026-10-12 定稿）：服务端不执行管控，只下发；面板按"谁执行"分三层
                List<string> sections = catalog.Sections();
                var unclassified = PolicyOwnership.UnclassifiedSections(sections);
                Check(unclassified.Count == 0, "7.12 客户端目录里每个分组都被归属表覆盖",
                    string.Join("；", unclassified));
                var unmatched = PolicyOwnership.UnmatchedPrefixes(sections);
                Check(unmatched.Count == 0, "7.13 归属表里每个前缀都能对上目录分组（改名不会静默失效）",
                    string.Join("；", unmatched));
                Check(PolicyOwnership.Of(sections.First(s => s.StartsWith("1、", StringComparison.Ordinal))) == PolicyTier.SelfProtect,
                    "7.14 电子教室保护 → 学生端自保（学生机执行，不是教师端的活）");
                Check(PolicyOwnership.Of(sections.First(s => s.StartsWith("14、", StringComparison.Ordinal))) == PolicyTier.SelfProtect,
                    "7.15 进程防拆加固 → 学生端自保");
                Check(PolicyOwnership.Of(sections.First(s => s.StartsWith("16、", StringComparison.Ordinal))) == PolicyTier.Classroom &&
                      PolicyOwnership.VisibleInPanel(sections.First(s => s.StartsWith("16、", StringComparison.Ordinal))),
                    "7.15b 防会话逃逸（换用户/新会话）→ 课堂管控，老师能调（v0.13 新增组）");
                Check(PolicyOwnership.Of(sections.First(s => s.StartsWith("15、", StringComparison.Ordinal))) == PolicyTier.ServerLocal &&
                      !PolicyOwnership.VisibleInPanel(sections.First(s => s.StartsWith("15、", StringComparison.Ordinal))),
                    "7.16 服务器统一管控 → 教师端自身，面板不显示（学生机不收这组值）");
                Check(PolicyOwnership.Of(sections.First(s => s.StartsWith("2、", StringComparison.Ordinal))) == PolicyTier.Classroom,
                    "7.17 网络与防火墙 → 课堂管控（老师常改）");
                Check(PolicyOwnership.SectionsOf(sections, PolicyTier.Classroom).Count >= 8 &&
                      PolicyOwnership.SectionsOf(sections, PolicyTier.SelfProtect).Count >= 4,
                    "7.18 两层分组数量合理（课堂 ≥8 / 自保 ≥4）");
                Check(PolicyOwnership.TierTitle(PolicyTier.Classroom).Contains("课堂") &&
                      PolicyOwnership.TierTitle(PolicyTier.SelfProtect).Contains("学生端") &&
                      PolicyOwnership.TierHint(PolicyTier.SelfProtect).Contains("学生机"),
                    "7.19 每层都有标题与说明（界面照着它讲「谁在干活」）");
                Check(catalog.Fields.Count(f => PolicyOwnership.VisibleInPanel(f.Section)) ==
                      catalog.Fields.Count - catalog.Fields.Count(f => PolicyOwnership.Of(f.Section) == PolicyTier.ServerLocal),
                    "7.20 面板显示数 = 全部 − 教师端自身数",
                    catalog.Fields.Count(f => PolicyOwnership.VisibleInPanel(f.Section)) + " / " + catalog.Fields.Count);

                // ================================================================ [8] seq 水位与重启防回退
                // 执行计划 §B2 不变量 8：服务重启后不得把 seq 回退到旧值。
                // 只在磁盘信封上做验签是查不出"签名合法的旧信封被放回磁盘"的，必须靠 seq 水位。
                Console.WriteLine("[8] seq 水位与重启防回退（安全不变量 8）");
                var wmPolicy = catalog != null
                    ? Json.ParseObject(catalog.DefaultPolicyJson)
                    : new Dictionary<string, object>();
                string envelopePath = null;
                string wmDir = Path.Combine(tmp, "watermark");
                var wmStore = new PolicyStore(wmDir, keys);
                wmStore.Load();
                Check(wmStore.Watermark == 0, "8.1 首次启动：水位为 0");
                wmStore.Save(wmPolicy, false, catalog?.DefaultPolicyJson);
                Check(wmStore.CurrentSeq == 1 && wmStore.Watermark == 1, "8.2 保存后水位跟随 seq=1");
                envelopePath = Path.Combine(wmDir, "policy.envelope");
                string firstEnvelope = File.ReadAllText(envelopePath, Encoding.UTF8);
                wmStore.Save(wmPolicy, false, catalog?.DefaultPolicyJson);       // seq=2
                Check(wmStore.CurrentSeq == 2 && wmStore.Watermark == 2, "8.3 再保存 seq=2、水位=2");

                // 攻击场景：把磁盘信封换成**签名合法**的旧信封（seq=1），然后"重启"
                File.WriteAllText(envelopePath, firstEnvelope, new UTF8Encoding(false));
                bool wmRollbackCaught = false;
                try { new PolicyStore(wmDir, keys).Load(); }                    // 故意不传 minSeq
                catch (InvalidDataException) { wmRollbackCaught = true; }
                Check(wmRollbackCaught, "8.4 重启加载：签名合法的旧信封被水位拦下（不传 minSeq 也拒）");

                // 水位文件损坏 → 告警降级（按 0），但不能阻断启动
                File.WriteAllText(Path.Combine(wmDir, "seq.highwater"), "{坏", new UTF8Encoding(false));
                var degraded = new PolicyStore(wmDir, keys);
                bool degradedOk = true;
                try { degraded.Load(); } catch { degradedOk = false; }
                Check(degradedOk && degraded.CurrentSeq == 1,
                    "8.5 水位文件损坏 → 告警按 0 处理，不阻断加载");

                // 只有水位、没有信封（换机/清空策略文件）：保存必须从水位继续，不复用已发布过的 seq
                string wmDir2 = Path.Combine(tmp, "watermark2");
                Directory.CreateDirectory(wmDir2);
                File.WriteAllText(Path.Combine(wmDir2, "seq.highwater"),
                    "{\"schemaVersion\":1,\"seq\":5}", new UTF8Encoding(false));
                var wmStore2 = new PolicyStore(wmDir2, keys);
                wmStore2.Load();
                Check(wmStore2.CurrentEnvelope == null && wmStore2.Watermark == 5,
                    "8.6 只有水位没有信封：水位仍然生效");
                wmStore2.Save(wmPolicy, false, catalog?.DefaultPolicyJson);
                Check(wmStore2.CurrentSeq == 6, "8.7 保存从水位继续（seq=6），不复用已发布过的 seq");

                // 原子写（§B4）：不留 .tmp 残留
                Check(!File.Exists(Path.Combine(wmDir2, "policy.envelope.tmp")) &&
                      !File.Exists(Path.Combine(wmDir2, "seq.highwater.tmp")),
                    "8.8 安全写入不留 .tmp 残留");

                // ================================================================ [9] 磁盘策略热拾取
                // 服务端内存策略只在启动时读一次；外部（CLI/脚本/管理端）保存的策略必须能被运行中的实例拾取，
                // 否则会出现"磁盘是新策略、/api/policy 还发旧信封"的静默不一致。口径与启动加载一致：先验签 + 比 seq。
                Console.WriteLine("[9] 磁盘策略热拾取（外部保存 / 防回退 / 坏文件）");
                string watchDir = Path.Combine(tmp, "watch");
                var live = new PolicyStore(watchDir, keys);
                live.Load();
                live.StartWatching();
                live.Save(catalog != null ? Json.ParseObject(catalog.DefaultPolicyJson) : new Dictionary<string, object>(),
                    false, catalog?.DefaultPolicyJson);
                Check(live.CurrentSeq == 1, "9.1 现行策略 seq=1（监视已开启）");

                string envelopeSeq1 = live.CurrentEnvelope;
                var writerStore = new PolicyStore(watchDir, keys);   // 模拟"另一个进程"保存
                writerStore.Load();
                writerStore.Save(catalog != null ? Json.ParseObject(catalog.DefaultPolicyJson) : new Dictionary<string, object>(),
                    false, catalog?.DefaultPolicyJson);
                bool picked = false;
                for (int i = 0; i < 50 && !picked; i++) { System.Threading.Thread.Sleep(100); picked = live.CurrentSeq == 2; }
                Check(picked, "9.2 外部保存 seq=2 → 运行中的实例自动拾取（无需重启）", "now=" + live.CurrentSeq);

                File.WriteAllText(Path.Combine(watchDir, "policy.envelope"), envelopeSeq1, new UTF8Encoding(false));
                System.Threading.Thread.Sleep(800);
                Check(live.CurrentSeq == 2, "9.3 有人把旧信封放回磁盘 → 拒绝（保留现行 seq=2）", "now=" + live.CurrentSeq);

                File.WriteAllText(Path.Combine(watchDir, "policy.envelope"), "LGSRV1|99|garbage", new UTF8Encoding(false));
                System.Threading.Thread.Sleep(800);
                Check(live.CurrentSeq == 2 && live.CurrentEnvelope != null,
                    "9.4 磁盘上是坏文件 → 拒绝且不影响现行策略", "now=" + live.CurrentSeq);
                live.StopWatching();

                // ================================================================ [10] 设备身份与策略应用状态（v0.10.4）
                // 为什么要有它：v0.10.3 之前教师端只能看到"最后心跳 seq"，看不出"这台到底应用成功没有"；
                // 而且身份只有机器名+IP（DHCP 一变就认不出同一台）。这里覆盖：新字段校验、身份聚合、
                // 克隆检测、以及 7 档状态计算的优先级。
                Console.WriteLine("[10] 设备身份与策略应用状态");
                var devOnline = new OnlineList();
                var devRate = new ConcurrentDictionary<string, ConcurrentQueue<DateTime>>();
                var devNow = DateTime.UtcNow;
                ushort devPort = 0;   // 占位，避免与 [6] 段变量重名
                Check(devPort == 0, "10.0 段落初始化（占位断言，保持计数稳定）");

                OnlineList online2 = new OnlineList();
                DateTime t0 = DateTime.UtcNow;
                Check(ApiServer.Checkin("{\"machine\":\"PC-OLD\"}", "10.1.1.1", online2, devRate, t0).Status == 200,
                    "10.1 旧客户端心跳（无 deviceId/appliedSeq）→ 200（向前兼容）");
                Check(DeviceModel.Compute(online2.Snapshot()[0], 5, "0.10.4", t0, online2.OfflineAfter) == DeviceStatus.Online,
                    "10.2 旧客户端只能判「在线」，不误报已应用");

                DateTime t1 = DateTime.UtcNow;
                Check(ApiServer.Checkin("{\"machine\":\"PC-NEW\",\"deviceId\":\"LG-0011223344556677\",\"appliedSeq\":5,\"status\":\"ok\"}",
                    "10.1.1.2", online2, devRate, t1).Status == 200, "10.3 新客户端心跳（带 deviceId/appliedSeq）→ 200");
                Check(DeviceModel.Compute(online2.Snapshot().First(r => r.Machine == "PC-NEW"), 5, "0.10.4", t1, online2.OfflineAfter) == DeviceStatus.Applied,
                    "10.4 appliedSeq = 服务端 seq → 「已应用」");
                Check(DeviceModel.Compute(online2.Snapshot().First(r => r.Machine == "PC-NEW"), 6, "0.10.4", t1, online2.OfflineAfter) == DeviceStatus.Pending,
                    "10.5 服务端已发布 seq=6 → 该机「待应用」");

                DateTime t2 = DateTime.UtcNow;
                ApiServer.Checkin("{\"machine\":\"PC-ERR\",\"deviceId\":\"LG-aaaabbbbccccdddd\",\"appliedSeq\":4,\"status\":\"error\",\"errorCode\":\"LG-POLICY-APPLY-FAILED\"}",
                    "10.1.1.3", online2, devRate, t2);
                Check(DeviceModel.Compute(online2.Snapshot().First(r => r.Machine == "PC-ERR"), 5, "0.10.4", t2, online2.OfflineAfter) == DeviceStatus.Error,
                    "10.6 status=error → 「应用失败」（优先于待应用）");

                DateTime t3 = DateTime.UtcNow;
                ApiServer.Checkin("{\"machine\":\"PC-SKEW\",\"deviceId\":\"LG-1111222233334444\",\"appliedSeq\":5,\"version\":\"0.9.1\"}",
                    "10.1.1.4", online2, devRate, t3);
                Check(DeviceModel.Compute(online2.Snapshot().First(r => r.Machine == "PC-SKEW"), 5, "0.10.4", t3, online2.OfflineAfter) == DeviceStatus.VersionSkew,
                    "10.7 客户端 0.9.x vs 服务端 0.10.x → 「版本不一致」（仍服务，只标注）");
                Check(!DeviceModel.VersionSkew("0.10.4", "0.10.4") && !DeviceModel.VersionSkew("0.10.9", "0.10.4"),
                    "10.8 同主次版本（补丁号不同）不算错配");

                // 克隆镜像：同一个 deviceId 从两台不同机器名上报
                DateTime t4 = DateTime.UtcNow;
                ApiServer.Checkin("{\"machine\":\"PC-CLONE-A\",\"deviceId\":\"LG-deadbeefdeadbeef\",\"appliedSeq\":5}",
                    "10.1.1.5", online2, devRate, t4);
                ApiServer.Checkin("{\"machine\":\"PC-CLONE-B\",\"deviceId\":\"LG-deadbeefdeadbeef\",\"appliedSeq\":5}",
                    "10.1.1.6", online2, devRate, t4);
                Check(DeviceModel.Compute(online2.Snapshot().First(r => r.Machine == "PC-CLONE-B"), 5, "0.10.4", t4, online2.OfflineAfter) == DeviceStatus.CloneSuspect,
                    "10.9 同一 deviceId 来自两台机器名 → 「疑似克隆（重复ID）」");
                Check(online2.Snapshot().Count(r => r.DeviceId == "LG-deadbeefdeadbeef") == 1,
                    "10.10 同一 deviceId 只算一台设备（按身份聚合，不按机器名重复计数）");

                DateTime t5 = DateTime.UtcNow;
                Check(ApiServer.Checkin("{\"machine\":\"PC-BAD\",\"deviceId\":123}", "10.1.2.1", online2, devRate, t5).Status == 400,
                    "10.11 deviceId 非字符串 → 400");
                Check(ApiServer.Checkin("{\"machine\":\"PC-BAD\",\"appliedSeq\":-1}", "10.1.2.2", online2, devRate, t5).Status == 400,
                    "10.12 appliedSeq 为负 → 400");
                Check(ApiServer.Checkin("{\"machine\":\"PC-BAD\",\"appliedSeq\":\"5\"}", "10.1.2.3", online2, devRate, t5).Status == 400,
                    "10.13 appliedSeq 是字符串 → 400");
                Check(ApiServer.Checkin("{\"machine\":\"PC-BAD\",\"status\":\"whatever\"}", "10.1.2.4", online2, devRate, t5).Status == 400,
                    "10.14 status 不在白名单（ok/error）→ 400");
                Check(ApiServer.Checkin("{\"machine\":\"PC-BAD\",\"errorCode\":\"" + new string('x', 65) + "\"}", "10.1.2.5", online2, devRate, t5).Status == 400,
                    "10.15 errorCode 超长（>64）→ 400");
                Check(ApiServer.Checkin("{\"machine\":\"PC-BAD\",\"deviceId\":\"" + new string('d', 65) + "\"}", "10.1.2.6", online2, devRate, t5).Status == 400,
                    "10.16 deviceId 超长（>64）→ 400");
                Check(online2.Count >= 5, "10.17 登记设备数含离线（含旧客户端按机器名兜底）", online2.Count + " 台");

                // ================================================================ [11] 设备分组（v0.11）
                // 口径：默认组 = policy.envelope（不变）；非默认组是覆盖层；
                // 解析 = deviceId 精确 → 机器名精确 → 未命中回落默认；seq 在所有组之间**全局单调**
                // （否则设备调组会拿到更小 seq 而被客户端正确拒绝，表现为"调了组却不生效"）。
                Console.WriteLine("[11] 设备分组（解析 / 按组下发 / 全局序号 / 持久化）");
                string grpDir = Path.Combine(tmp, "groups");
                Directory.CreateDirectory(grpDir);
                var grpStore = new PolicyStore(grpDir, keys);
                grpStore.Load();
                grpStore.Save(catalog != null ? Json.ParseObject(catalog.DefaultPolicyJson) : new Dictionary<string, object>(),
                    false, catalog?.DefaultPolicyJson);           // 默认组 seq=1
                var groups = new GroupStore(grpDir);
                groups.Load();

                Check(ApiServer.GetVersion(grpStore, groups, null, null).Body == ApiServer.GetVersion(grpStore).Body,
                    "11.1 没有任何组 / 不带身份 → 与默认策略完全一致（v0.10 行为不变）");

                DeviceGroup gA = groups.Add("高一(3)班机房");
                DeviceGroup gB = groups.Add("信息学考试机房");
                Check(groups.Groups.Count == 2 && groups.Groups.All(g => g.Id.StartsWith("g-")),
                    "11.2 新建组（自动分配 g- 开头 ID）");

                groups.Assign(gA.Id, "LG-aaaa000011112222", "PC-101");
                Check(groups.Resolve("LG-aaaa000011112222", "别的名字") == gA,
                    "11.3 按 deviceId 精确命中（机器名不同也认得）");
                Check(groups.Resolve(null, "PC-101") == gA, "11.4 旧客户端按机器名兜底命中");
                Check(groups.Resolve("LG-9999", "PC-999") == null, "11.5 都不命中 → 回落默认策略");

                groups.Assign(gB.Id, "LG-aaaa000011112222", "PC-101");
                Check(groups.Resolve("LG-aaaa000011112222", "PC-101") == gB && gA.DeviceIds.Count == 0,
                    "11.6 一台设备只属于一个主组（调组会自动从原组移走）");

                // 按组下发：seq 必须比默认组大（全局单调）
                var grpPolicy = catalog != null ? Json.ParseObject(catalog.DefaultPolicyJson) : new Dictionary<string, object>();
                PathAccess.Set(grpPolicy, "StartDelaySeconds", 999);
                ulong seqA = GroupPublisher.Publish(keys, gB, grpPolicy, false, () => grpStore.Watermark, s => grpStore.MarkPublished(s));
                Check(seqA == 2, "11.7 组下发拿到全局下一个 seq（默认组 1 → 组 2）", "seq=" + seqA);
                Check(!string.IsNullOrEmpty(gB.Envelope) && gB.Seq == seqA, "11.7 组信封已生成并记录 seq");

                ApiServer.ApiReply groupVer = ApiServer.GetVersion(grpStore, groups, "LG-aaaa000011112222", "PC-101");
                Check(groupVer.Status == 200 && groupVer.Body.Contains("\"seq\":2"),
                    "11.8 带身份请求 → 返回该组策略的版本", groupVer.Body);
                ApiServer.ApiReply groupPol = ApiServer.GetPolicy(grpStore, groups, "LG-aaaa000011112222", "PC-101");
                Check(groupPol.Status == 200 && groupPol.Body == gB.Envelope, "11.8 带身份请求 → 拿到该组信封原文");
                Check(ApiServer.GetPolicy(grpStore, groups, null, "PC-999").Body == grpStore.CurrentEnvelope,
                    "11.8 未命中组的设备 → 仍拿默认信封");

                // 再发默认组：seq 必须比组 seq 大（否则组设备会拒收默认策略）
                PolicyVersion def2 = grpStore.Save(grpPolicy, false, catalog?.DefaultPolicyJson);
                Check(def2.Seq > seqA, "11.9 之后发布默认组 → seq 继续增大（跨组全局单调）",
                    "默认 seq=" + def2.Seq + " > 组 seq=" + seqA);

                // 持久化往返
                groups.Save();
                var groupsReloaded = new GroupStore(grpDir);
                groupsReloaded.Load();
                DeviceGroup rB = groupsReloaded.Get(gB.Id);
                Check(groupsReloaded.Groups.Count == 2 && rB != null && rB.Name == "信息学考试机房" &&
                      rB.Envelope == gB.Envelope && rB.Seq == gB.Seq && rB.DeviceIds.Count == 1 &&
                      rB.Machines.Count == 1,
                    "11.10 分组持久化：名称/成员/信封/seq 往返一致（schemaVersion=1）");

                // 删组 → 设备回默认
                groupsReloaded.Remove(gB.Id);
                Check(groupsReloaded.Resolve("LG-aaaa000011112222", "PC-101") == null, "11.11 删组后设备回到默认策略");
                Check(groupsReloaded.GroupNameOf("LG-aaaa000011112222", "PC-101") == GroupStore.DefaultGroupName,
                    "11.11 组名显示回落为「" + GroupStore.DefaultGroupName + "」");

                // ================================================================ [12] 临时课堂状态层（v0.12）
                // 有效策略 = 基础策略 ⊕ 临时覆盖；覆盖在**发布时**合成、照旧签名 + 排 seq 下发（客户端零改动）。
                // 三件事必须成立：合并规则确定可测、三层解析（临时 > 分组 > 默认）、到期/重启都能可靠回到基础策略。
                Console.WriteLine("[12] 临时课堂状态层（合并规则 / 三层解析 / 到期恢复 / 重启不残留）");
                // 用独立作用域包起来：这一节的临时变量名（t1/mgr/…）不必和外层的重名（外层同名变量会遮蔽，
                // 是本文件的老毛病之一——加一层 {} 最省事也最不容易出错）
                {

                // ---- 合并规则（纯函数，先钉死语义再谈编排）
                var mBase = Json.ParseObject("{\"A\":{\"x\":1,\"y\":2},\"B\":[1,2,3],\"C\":\"keep\",\"D\":false}");
                string mBaseDump = PolicyMerge.Dump(mBase);
                var mMerged = PolicyMerge.Merge(mBase, Json.ParseObject("{\"A\":{\"y\":9},\"B\":[7],\"E\":\"new\"}"));
                Check(Convert.ToInt32(PathAccess.Get(mMerged, "A.x")) == 1 &&
                      Convert.ToInt32(PathAccess.Get(mMerged, "A.y")) == 9,
                    "12.1 对象递归合并：只改补丁里出现的键（A.x 保持 1、A.y 变 9）");
                // 数组元素类型随序列化器不同（object[] / ArrayList），这里比"规范化摘要"最稳
                Check(PolicyMerge.Dump(PathAccess.Get(mMerged, "B")) == "[7]",
                    "12.2 数组**整体替换**而不是追加（能临时『只放行 3 个进程』）",
                    PolicyMerge.Dump(PathAccess.Get(mMerged, "B")));
                Check((string)PathAccess.Get(mMerged, "C") == "keep" && Equals(PathAccess.Get(mMerged, "D"), false) &&
                      (string)PathAccess.Get(mMerged, "E") == "new",
                    "12.3 补丁没提到的键一个都不动；补丁新增的键被加入");
                Check(PolicyMerge.Dump(PolicyMerge.Merge(mBase, new Dictionary<string, object>())) == mBaseDump,
                    "12.4 空补丁 = base 原样（『恢复/回槽』语义的地基）");
                Check(PolicyMerge.Dump(mBase) == mBaseDump,
                    "12.5 合并不修改入参（base 摘要不变，别把面板底稿改脏）");
                string dump1 = PolicyMerge.Dump(PolicyMerge.Merge(mBase, Json.ParseObject("{\"A\":{\"y\":9},\"E\":\"new\",\"B\":[7]}")));
                string dump2 = PolicyMerge.Dump(PolicyMerge.Merge(mBase, Json.ParseObject("{\"B\":[7],\"E\":\"new\",\"A\":{\"y\":9}}")));
                Check(dump1 == dump2, "12.6 确定性：补丁键顺序不同 → 合并结果完全一致");

                // ---- 落地测试环境：一个带分组的策略仓库
                string tdir = Path.Combine(tmp, "v12temp");
                Directory.CreateDirectory(tdir);
                var tStore = new PolicyStore(tdir, keys);
                tStore.Load();
                var tGroups = new GroupStore(tdir);
                tGroups.Load();
                Dictionary<string, object> tPolicy = Json.ParseObject(catalog.DefaultPolicyJson);
                tStore.Save(tPolicy, false, catalog.DefaultPolicyJson);                       // 默认组 seq=1
                DeviceGroup tGx = tGroups.Add("机房X");
                var tPolicyX = Json.ParseObject(catalog.DefaultPolicyJson);
                PathAccess.Set(tPolicyX, "StartDelaySeconds", 777);
                GroupPublisher.Publish(keys, tGx, tPolicyX, false, () => tStore.RefreshWatermark(), s => tStore.MarkPublished(s));
                tGroups.Assign(tGx.Id, "LG-inside-group", "PC-X");
                tGroups.Save();

                var mgr = new TempStateManager(tdir, keys, tStore, tGroups);
                mgr.Load(DateTime.UtcNow);
                Check(!mgr.Active, "12.7 没有临时状态文件 → Active=false（与 v0.11 行为一致）");

                var presetList = TempPresets.Load(tdir);
                Check(presetList.Count >= 3 && File.Exists(TempPresets.PathOf(tdir)),
                    "12.8 预设表缺失 → 自动落一份内置示例（3 个，老师可直接编辑）");
                Check(TempPresets.Find(presetList, "考试模式") != null && TempPresets.Find(presetList, "不存在") == null,
                    "12.8 预设按名字命中（找不到返回 null）");
                Check(TempPresets.Find(presetList, "全员暂停")?.Paused == true,
                    "12.8 预设可以带『全员暂停』（patch 为空也算一个有效的临时状态）");

                TempState v12t1 = mgr.Start("考试模式", "{\"Usb\":{\"Enabled\":true,\"AllowStorage\":false}}", false, null,
                    "自检", DateTime.UtcNow);
                Check(v12t1.DefaultEnvelope != null && v12t1.GroupEnvelopes.ContainsKey(tGx.Id),
                    "12.9 开始临时状态：默认组 + 每个已下发分组的信封都签好了");
                Check(PolicyEnvelope.ParseHeader(v12t1.DefaultEnvelope).Seq > tStore.Version().Seq,
                    "12.9 临时信封 seq 严格大于基础信封（否则学生机按防回滚拒收）");

                // ---- 三层解析：临时 > 分组 > 默认
                Check(ApiServer.GetPolicy(tStore, tGroups, mgr, "LG-nobody", "PC-9").Body == v12t1.DefaultEnvelope,
                    "12.10 未命中分组的设备 → 拿默认组的临时信封");
                Check(ApiServer.GetPolicy(tStore, tGroups, mgr, "LG-inside-group", "PC-X").Body == v12t1.GroupEnvelopes[tGx.Id],
                    "12.10 组内设备 → 拿『该组自己的基础策略 ⊕ 同一份临时覆盖』（不是默认组那份）");
                var tGxEnv = PolicyEnvelope.Parse(v12t1.GroupEnvelopes[tGx.Id], keys.Rsa);
                var tGxJson = Json.ParseObject(tGxEnv.PolicyJson);
                Check(tGxEnv.Verified && Convert.ToInt32(PathAccess.Get(tGxJson, "StartDelaySeconds")) == 777 &&
                      Equals(PathAccess.Get(tGxJson, "Usb.AllowStorage"), false),
                    "12.10 组临时信封的内容核对：保留了本组的 777，也应用了临时覆盖的 AllowStorage=false");

                // ---- 到期 → 自动恢复基础策略
                TempState v12t2 = mgr.Start("考试模式（会到期）", "{\"StartDelaySeconds\":3333}", false,
                    DateTime.UtcNow.AddSeconds(1), "自检", DateTime.UtcNow);
                bool expired = false;
                for (int i = 0; i < 40 && !expired; i++)
                {
                    System.Threading.Thread.Sleep(200);
                    expired = mgr.ExpireIfDue(DateTime.UtcNow);
                }
                Check(expired && !mgr.Active, "12.11 到点自动结束（ExpireIfDue=true、Active=false）");
                Check(!File.Exists(TempState.PathOf(tdir)), "12.11 临时状态文件已删除（不留幽灵覆盖）");
                var tBase = PolicyEnvelope.Parse(tStore.CurrentEnvelope, keys.Rsa);
                Check(tBase.Verified && !tBase.PolicyJson.Contains("3333") && tBase.Seq > v12t2.MaxSeq,
                    "12.11 默认组回到基础策略：内容不含临时改动，且 seq 比临时状态更高（学生机才会离开临时策略）");
                var tGxBase = PolicyEnvelope.Parse(tGroups.Get(tGx.Id).Envelope, keys.Rsa);
                Check(Convert.ToInt32(PathAccess.Get(Json.ParseObject(tGxBase.PolicyJson), "StartDelaySeconds")) == 777 &&
                      tGxBase.Seq > v12t2.MaxSeq,
                    "12.11 分组也回到自己的基础策略（777 保留），seq 同样前进");

                // ---- 临时暂停：paused 位在临时状态里叠加、结束后回落
                TempState v12t3 = mgr.Start("临时全员暂停", "{}", true, null, "自检", DateTime.UtcNow);
                Check(PolicyEnvelope.ParseHeader(v12t3.DefaultEnvelope).Paused,
                    "12.12 临时状态可以带『全员暂停』（信封 paused=1）");
                mgr.End("自检结束", DateTime.UtcNow);
                Check(!PolicyEnvelope.ParseHeader(tStore.CurrentEnvelope).Paused &&
                      !File.Exists(TempState.PathOf(tdir)),
                    "12.12 人工结束后 paused 回到基础值（0），临时文件删除");

                // ---- 服务端重启：不允许"睡一觉还在考试模式"
                TempState v12t4 = mgr.Start("昨晚的考试模式", "{\"StartDelaySeconds\":4444}", false,
                    DateTime.UtcNow.AddSeconds(1), "自检", DateTime.UtcNow);
                var restarted = new TempStateManager(tdir, keys, tStore, tGroups);
                restarted.Load(DateTime.UtcNow.AddSeconds(3));      // "重启"发生在过期之后
                Check(!restarted.Active && !File.Exists(TempState.PathOf(tdir)),
                    "12.13 重启时发现已过期 → 直接丢弃（Active=false、文件已删）");
                Check(tStore.Version().Seq > v12t4.MaxSeq,
                    "12.13 并且把基础策略重发了一次（新 seq）——否则学生机会永久卡在临时策略上");

                // ---- 基础策略在临时状态期间被改：临时层必须按新基础重算
                TempState v12t5 = mgr.Start("考试模式", "{\"StartDelaySeconds\":5555}", false, null, "自检", DateTime.UtcNow);
                ulong beforeRefresh = v12t5.MaxSeq;
                var tNewBase = Json.ParseObject(catalog.DefaultPolicyJson);
                PathAccess.Set(tNewBase, "StartDelaySeconds", 888);
                tStore.Save(tNewBase, false, null);
                mgr.RefreshIfActive("自检：基础策略变了");
                var tAfter = PolicyEnvelope.Parse(mgr.Current.DefaultEnvelope, keys.Rsa);
                Check(Convert.ToInt32(PathAccess.Get(Json.ParseObject(tAfter.PolicyJson), "StartDelaySeconds")) == 5555 &&
                      mgr.Current.MaxSeq > beforeRefresh,
                    "12.14 临时状态跟随新基础重算（覆盖仍是 5555、seq 前进）");
                mgr.End("自检结束", DateTime.UtcNow);
                Check(Convert.ToInt32(PathAccess.Get(Json.ParseObject(PolicyEnvelope.Parse(tStore.CurrentEnvelope, keys.Rsa).PolicyJson),
                        "StartDelaySeconds")) == 888,
                    "12.14 结束后回到的是**新**基础策略（888），不是开临时状态时的旧基础");

                // ---- 覆盖范围口径：临时状态期间新建的组，**刷新后**才纳入覆盖（合成用的必须是它自己的基础策略）
                TempState t6 = mgr.Start("考试模式", "{\"StartDelaySeconds\":6666}", false, null, "自检", DateTime.UtcNow);
                DeviceGroup tGy = tGroups.Add("后来建的组");
                var tPolicyY = Json.ParseObject(catalog.DefaultPolicyJson);
                PathAccess.Set(tPolicyY, "StartDelaySeconds", 222);
                GroupPublisher.Publish(keys, tGy, tPolicyY, false, () => tStore.RefreshWatermark(), s => tStore.MarkPublished(s));
                tGroups.Assign(tGy.Id, "LG-late", null);
                tGroups.Save();
                Check(ApiServer.GetPolicy(tStore, tGroups, mgr, "LG-late", null).Body == tGroups.Get(tGy.Id).Envelope,
                    "12.15 临时状态期间新建的组：在刷新前拿自己的组策略（绝不继承别人组的合成结果）");
                mgr.RefreshIfActive("自检：新建组后刷新");
                var tGyEnv = PolicyEnvelope.Parse(mgr.Current.GroupEnvelopes[tGy.Id], keys.Rsa);
                var tGyJson = Json.ParseObject(tGyEnv.PolicyJson);
                Check(tGyEnv.Verified && Convert.ToInt32(PathAccess.Get(tGyJson, "StartDelaySeconds")) == 6666 &&
                      ApiServer.GetPolicy(tStore, tGroups, mgr, "LG-late", null).Body == mgr.Current.GroupEnvelopes[tGy.Id],
                    "12.15 刷新后它也被临时覆盖纳入（= 它自己的基础策略 ⊕ 同一份临时补丁）");
                mgr.End("自检结束", DateTime.UtcNow);

                // ---- 有人手改 temp-state.json 塞假信封 → 拒绝采用并清理
                var forged = new TempState
                {
                    Id = "t-forged", Name = "手改的", PatchJson = "{}",
                    DefaultEnvelope = "LGSRV1|99|2026-01-01T00:00:00Z|0|{}|AAAA",   // 签名是假的
                    StartedAtUtc = DateTime.UtcNow,
                };
                forged.Save(tdir);
                var mgr3 = new TempStateManager(tdir, keys, tStore, tGroups);
                mgr3.Load(DateTime.UtcNow);
                Check(!mgr3.Active && !File.Exists(TempState.PathOf(tdir)),
                    "12.16 临时状态里的信封验签不过 → 拒绝采用并删掉文件（绝不把不可信内容发出去）");
                }
            }
        }

        private static int FindFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
