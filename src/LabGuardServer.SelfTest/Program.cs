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
using LabGuardServer.Core.Security;
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

                // ================================================================ [7] 密码
                Console.WriteLine("[7] 口令散列");
                string hash = PasswordHasher.Create("a1b2c3");
                Check(PasswordHasher.Verify("a1b2c3", hash), "7.1 正确密码验证通过");
                Check(!PasswordHasher.Verify("a1b2c4", hash), "7.2 错误密码拒绝");
                Check(PasswordHasher.Validate("123456") != null && PasswordHasher.Validate("a1b2c3") == null,
                    "7.3 弱口令检出 / 正常口令放行");
                Check(!PasswordHasher.Verify("a1b2c3", "not-a-hash"), "7.4 散列格式损坏安全拒绝");
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
