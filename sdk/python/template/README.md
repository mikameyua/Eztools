# 新建一个 Eztools 工具

**目标：新增一个工具 = 新建一个目录 + 写 `tool.json` + 写 `main.py`。宿主一行代码都不用改。**

## 步骤

1. 在 `tools/` 下新建目录，名字就是工具 id（小写 + 连字符）：

   ```bash
   mkdir tools/my-tool
   ```

2. 把本目录的 `tool.json` 与 `main.py` 复制进去。

3. 改 `tool.json`：
   - `id` 必须与目录名一致，且命令 id 用 `<id>.xxx` 前缀
   - `contributes.commands[].handler` 写你打算实现的 handler 名
   - `config` 里声明的属性会自动出现在设置页（P1）上，你没写过的配置项不要声明

4. 改 `main.py`：用 `@tool.handler("名字")` 注册，名字与上一步一致。

5. 验证：

   ```bash
   ezt list                      # 应该能看到你的工具被自动发现
   ezt info my-tool              # 看贡献点与配置 schema 是否被正确解析
   ezt invoke my-tool.run --text hello
   ```

## 需要第三方依赖时（例如 Pillow / numpy）

不要 `pip install` 到全局，也不建议打包。把 wheel 解到工具目录的 `Lib/` 即可：

```bash
pip install --target "tools/my-tool/Lib" --only-binary=:all: pillow
```

SDK 会把 `<工具目录>` 与 `<工具目录>/Lib` 自动加进 `sys.path`。

> 实测依据（spike 第六节）：同一 Windows 平台且 Python 版本由宿主固定时，
> 含编译代码的 wheel 直接 vendor 就能用；`--onefile` 打包每次启动多付约 900ms，
> 能不用就不用。

## 常见坑

| 症状 | 原因 |
|---|---|
| 调用超时 | 往 **stdout** 写了非 JSON 内容（污染分帧）。调试输出一律走 stderr 或用 `tool.log()` |
| 中文乱码 | 没有在入口 `import eztools`（SDK 会固定 stdio 编码），或自己 `reconfigure` 成了别的编码 |
| 宿主报"未注册 handler" | `tool.json` 的 `handler` 名与 `@tool.handler(...)` 不一致 |
| 工具不被发现 | `tool.json` 校验失败。跑 `ezt doctor` 看具体诊断（宿主只记日志、不弹窗） |
| 报"文件不存在"但文件明明在 | 工具进程的工作目录是**工具目录**，不是调用方的 cwd。工具内不要依赖相对路径；命令行用 `ezt invoke ... --path <相对路径>` 时 CLI 会自动绝对化，其他参数请自行传绝对路径 |
