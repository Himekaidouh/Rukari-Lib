# Stable 用户包验收绑定

`src/scripts/New-UserPackages.ps1` 默认生成 Candidate。Candidate 不要求用户验收记录；如提供记录，仍只保存外部文件引用和 SHA256，不因此改成 Stable。

Stable 必须显式指定 `-ReleaseChannel Stable -UserAcceptancePath '本次验收.json'`。自由文本、历史发布摘要以及旧格式 JSON 会报错；不能把旧 1.4.x 使用反馈用于新 X 轴、双范围镜头等版本的正式标签。脚本核对证据与当前已验证打包 stage 后，才创建用户 ZIP。

## 记录格式

使用 UTF-8 JSON，SchemaVersion 为整数 1，Approved 必须是 JSON 布尔值 `true`。先准备 `Approved=false` 的记录，完成真人验收并记录实际确认来源后才能改为 `true`。字符串 `"true"`、`"false"` 或数字不能代替批准。

必需字段如下：

| 字段 | 内容 |
| --- | --- |
| SchemaVersion | 整数 `1` |
| Kind | `user-package-acceptance` |
| Target | `AA 1.0 fix6` |
| Approved | 真人确认后填布尔值 `true` |
| ConfirmedAt | 真人确认时间，ISO 格式，带 `Z` 或时区偏移 |
| HumanScope | 实际确认的操作和范围；未测项目继续明确保留 |
| Evidence | 用户原话、会话或本地证据来源；不得编造确认 |
| ToolNativeValidation | 保持 `not-run` |
| BuildReceiptSHA256 | 本次 stage 的 `build-receipt.json` SHA256，与 `package-receipt.json` 的同名字段一致 |
| Packages | 恰好三个用户包，各含 Name、Version、Dlls |

Packages 必须包含 `RukariLib`、`更多的画面效果`、`人物配音支持` 各一次。版本取本次 `package-receipt.json` 的 Packages；不能依赖文档中的历史版本号。Dlls 是数组，每项为 `{ "Path": "规范包路径", "SHA256": "64位十六进制哈希" }`，路径和哈希取同一收据的 Files：

| 包 | 必须绑定的 DLL |
| --- | --- |
| RukariLib | `Rukari.Lib.dll`、`Rukari.Lib.Runtime.dll` |
| 更多的画面效果 | `Rukari.MoreEffects.dll`、`AzureArchive.VideoTools.Core.dll`、`AzureArchive.VideoTools.Formats.dll` |
| 人物配音支持 | `Rukari.CharacterVoice.dll` |

Path 完整形态为 `mods/模组名/本次版本/DLL文件名`，使用正斜线和收据中的精确大小写。漏项、重复包或 DLL、额外路径、其他版本、其他构建收据以及不同 DLL 字节均被拒绝。Spine 参与完整构建，但不进入这三个用户包，故不将第七个 Spine DLL 加入该验收记录。

不绑定每次生成时间不同的 `package-receipt.json` 文件哈希；绑定的是它引用的本次构建收据哈希，以及它已核对的三包版本和六个 DLL。重新打包同一已验收构建可以复用记录；重新构建后收据哈希或 DLL 发生变化，须重新核对对应验收来源。

## 工具检查与真人确认

人工 Approved 只表明 HumanScope 中记录的用户确认。构建和用户包收据的 `Native=not-run` 保留原义，脚本成功不把它升级成原生测试通过，不扩大为全部演出、所有第三方模组或全部资源均已验收。

验收 JSON 的解析内容和 SHA256 来自同一次文件字节读取。打包最终交付前再次核对验收文件与已验证 stage 收据，修改证据会使打包失败。用户包回执保留明确绑定与人工范围，证据和构建收据均在用户 ZIP 外。

## 回归验证

用已有完整打包 stage 运行：

    pwsh -File src/scripts/Test-UserPackageAcceptance.ps1 -ValidatedPackageDirectory '已有完整打包stage目录'

验证脚本使用系统临时目录中的合成证据和既有产物，不构建、部署、改 Profile 或操作游戏。它验证旧自由文本、版本和构建收据不符、DLL 改变、非布尔批准、重复或缺项及 Native 升级均被拒绝；同时调用真实用户 ZIP 入口验证默认 Candidate 仍可引用旧记录、绑定完整的合成 Stable 夹具可生成三包且 Native 仍为 not-run。完整四包核验入口在夹具中由复制器代替，不将这项回归测试表述为真实发行验收。

验证后清理经过绝对路径边界核对的临时目录。如果外部扫描程序锁定复制的 DLL，脚本报告保留位置，不操作扫描程序、不改动原始产物。新增或修改打包脚本后，生产打包仍须由完整构建刷新源码输入回执；回归夹具不会替代该检查。
