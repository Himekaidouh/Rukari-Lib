# Rukari lib 共享 UI 素材

这份皮肤仅随 Rukari lib Runtime 分发。功能 Mod 通过共享 UI 使用，不要复制到自己的包中。

- `atlases/Common.png` 与 `metadata/Common.json` 来源于本地 AA 1.0 先行版 `AzureArchive_Data/sharedassets0.assets` 的 Common NGUI 图集导出（2026-09-13）。
- `decorations/Popup_Img_Deco_1.png`、`Popup_Img_Deco_2.png` 是同次本地导出的窗口装饰，已与 AA 设置窗口所引用的素材核对。
- 图片按原样复制；元数据仅将本机绝对来源路径改为相对的来源说明，保留全部精灵坐标和切片边界。
- 此目录包含第三方游戏美术，不属于 Rukari 原创美术，也不适用仓库代码的 MIT 许可。素材权利归各自权利人；随包提供不表示取得官方背书或额外的素材授权。

皮肤四个数据文件由 `ui-assets.sha256` 配对。运行时从自己 DLL 旁的 `ui` 目录一次读取并验证，缺失或校验失败时整体使用程序绘制的备用外观。请勿单独替换其中一张图集或坐标文件。
