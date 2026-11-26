# 更新包布局协议（v1.9.3 起）

v1.9.3 过渡版保留根目录 `ASPAssistant.App.exe` 和现有依赖位置，使 v1.9.2 及兼容的旧更新器仍可安装它。它能读取后续发布包中的可选 `update-layout.json`。

v1.10.0 发布包的根目录入口为 `HongxueCompanion.exe`。v1.9.9 用户可在助手内检查更新。仍依赖旧入口 `ASPAssistant.App.exe` 的用户请下载完整包，解压到新目录后使用新入口。

清单位于安装包根目录，与 LICENSE、NOTICE 同层。ZIP 可以包含一个顶层文件夹，也可以直接包含包文件。版本 1 示例：

```json
{
  "formatVersion": 1,
  "executable": "app/HongxueCompanion.exe"
}
```

`executable` 声明真正的主程序，不是仅负责转发的启动器。当前明确支持三个值：`ASPAssistant.App.exe`、`HongxueCompanion.exe`、`app/HongxueCompanion.exe`。未提供清单时仅使用原入口 `ASPAssistant.App.exe`。未知格式、未知路径、越界路径、损坏清单或缺少指定主程序均拒绝更新，不回退猜测。

更新器整目录暂存并替换安装根目录，使用声明的主程序目录作为工作目录，等待该主进程通过 `ASPA_UPDATE_STARTUP_RESULT` 报告就绪。只有确认就绪后才删除旧备份。启动失败且新进程已退出时，恢复旧安装并使用旧文件名重启；新进程仍在运行但未确认就绪时保留备份，不强行终止它。

过渡版当前安装根目录就是 `AppContext.BaseDirectory`。未来将主程序移入 `app/` 时，整理版本自身的更新器也必须显式识别安装根目录，不能直接把 `app/` 当作整包替换目标；这一改造在整理版完成。整理版内应继续保留本协议及就绪确认。

新目录包必须包含完整的程序、运行库、资源与许可文件；清单与包一起由公开 SHA256SUMS 校验。配置继续位于用户目录，发布包不携带个人阵容、追踪、日志或开发证据。

发布整理版时，已安装 v1.9.3 的用户可自动迁移。仍使用旧版本而跳过过渡版的用户需要手动下载整理版或先安装过渡版，不能仅依靠 GitHub latest 保证逐版升级。
