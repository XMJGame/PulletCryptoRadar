# Vue 迁移验收

日期：2026-10-29（上海时间）。工程：PulletCryptoRadar。

## 运行架构

前端采用 Vue 3、TypeScript、Vite、Pinia、Vue Router。六个页面共用 App.vue 的布局、导航和选中状态；导航位置在路由切换时保持一致。市场、异动、通知、衍生品、回放与评估均迁移，Phase 7 增加资产映射。

ASP.NET Core 提供现有 API、后台采集、通知和 SQLite；SPA 产物由 Host/wwwroot 提供，页面刷新支持路由回退，未知 API 返回 JSON 404。已移除 Blazor 服务、组件端点及运行依赖。原 Web 和 Razor 文件保留作为历史参考，未参与构建。

共享轮询管理组件卸载取消、请求互斥、失败缓存和错误提示。行情 Unknown、Stale、源时间以及小币价格展示口径保留。金额运算和策略计算仍在 .NET decimal 后端；浏览器数值只供显示。

## 开发与发布

前端源文件位于 frontend/src；npm ci 后 npm run dev 启动开发页面，API 代理至本机 5218。npm run build 执行 vue-tsc 检查并生成生产产物。npm test 运行前端边界测试。

scripts/Publish.ps1 串行执行依赖恢复、6 项前端测试、类型检查、生产构建、221 项 .NET Release 测试和 Windows 独立发布。保留既有配置，补入缺失顶层节。

## 验证结果

六个页面 HTTP 200，均加载 Vue 模块且不含 blazor.web.js。浏览器逐页切换：每页只有一个选中项，导航纵坐标一致（170、229、287、345、403、461），无页面横向溢出，无控制台 warning/error。见 Phase07-NavigationValidation.json。

市场搜索、资产候选选择、目录搜索及保存确认保护已通过界面检查。回放与评估的历史复核和回放使用真实后端输入，不生成虚构历史；样本未成熟仍显示 Unknown。人工映射写入、版本冲突、持久化恢复和批次事务通过隔离数据库集成测试，验收没有确认任何真实资产身份。

原五页迁移阶段的导航证据见 Vue-Migration-Validation.json；Phase 7 六页和 API 证据见 Phase07-LiveValidation.json。
