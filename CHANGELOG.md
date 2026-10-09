# 更新记录

## v0.10（未发布 · 首版）

- 仓库初始化：README / LICENSE / CHANGELOG / 通信协议草案（`docs/protocol-v1.md`）。
- **M1 服务端最小可用**：
  - `LabGuardServer.Core`：LGSRV1 信封组装/解析/验签（RSA-2048-SHA256，**显式 `RSACryptoServiceProvider(2048)`，net48 的 `RSA.Create().KeySize` 无效**）；DPAPI 加密密钥库 + ACL 加固；catalog.json 契约加载；PolicyStore（seq 单调 + 签名信封落盘/加载，**加载先验签后提交、支持 minSeq 防回滚、拒绝时保留内存现行信封**）；HttpListener API（version/policy/checkin，同 IP 限速 + 限速表防泄漏清理）；在线列表；口令散列（与客户端同算法）。
  - `LabGuardServer`：WinForms 托盘（首跑设密码 + 密码门禁 + 指数退避）、catalog 动态渲染策略面板、全员暂停/恢复、配对文件导出。
  - `LabGuardServer.SelfTest`：**64 条断言全绿**（信封/密钥/目录/策略/API/端口冒烟/口令 + 红队回归 12 条）。
- **M1 子代理互测**（协议符合性代理 = Python+openssl 独立实现学生机端，10/10 PASS；红队代理 40 项攻击）修复：
  - 慢 POST 全服务阻塞 → 每连接线程池 + body 限时 5s（408）+ Content-Length 先验（413）；
  - 字段类型错乱 500 → checkin 严格类型校验 400；
  - HEAD 请求挂死 → HEAD 不写 body + 失败 Abort，GET 端点接受 HEAD；
  - 协议行为定稿入 `docs/protocol-v1.md`（状态码、限速、checkin 字段校验表、磁盘信封防回滚）。
- 版本号与客户端仓库 [ikeee/labguard](https://github.com/ikeee/labguard) **lockstep**（同版本号同日发版）。
