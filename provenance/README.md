# 来源记录

original-source-files.json 保留原始稳定基线输入哈希；project-inputs.json 记录编译闭包。repository-validation.json 是初始上传验证。后续审查清理及实际差异见 docs/08_发布前文件审查.md 和 repository-audit.json。原始哈希不代表后续版本始终逐字节相同。构建输出保留在本地 src/artifacts，不提交。

原始来源清单保留已删除的 VoiceOverrideCleanupPolicy 和清理测试文件名，仅记录初始输入。当前项目输入改为只读诊断文件。用户确认删除的验证见 voice-cleanup-removal.json。
