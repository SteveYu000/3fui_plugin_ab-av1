# FFmpegFreeUI AB-AV1 自动 CRF 插件

本插件会读取 FFmpegFreeUI v6 预设，将影响画面质量的编码参数交给 `ab-av1 crf-search` 测试；找到目标 VMAF 对应的 CRF 后，仅替换预设中的 CRF，再把正式编码任务加入 3FUI 原生编码队列。

当前版本：`0.4.3`。已按 FFmpegFreeUI `6.1.31` 源码中的官方加载器和回调签名验证。

![官方 API 任务管理界面](./images/界面预览.png)

## 使用的官方接口

- `SetHost_AddCustomWinformPanel`：注册左侧 `AB-AV1` 页面。
- `SetHost_AddMissionToQueueWith3fuiFile`：用搜索完成后的临时 v6 预设加入 3FUI 原生编码队列。

## 安装

1. 编译或取得 `dist\FFmpegFreeUI.AbAv1.3fui.dll`。
2. 将插件 DLL 和 `ab-av1.exe` 放进 FFmpegFreeUI 的同一个 `Plugin` 目录：

```text
FFmpegFreeUI.exe
Plugin\
├─ FFmpegFreeUI.AbAv1.3fui.dll
└─ ab-av1.exe
```

3. 确保 `ffmpeg` 可通过 FFmpegFreeUI 的当前目录或系统 `PATH` 正常找到，并且该 FFmpeg 包含 `libvmaf`。
4. 完全退出并重新启动 FFmpegFreeUI。左侧应出现 `AB-AV1`。

## 基本流程

1. 在 3FUI 中设置 `libsvtav1`、preset、像素格式、SVT 高级参数、音频、字幕、附件、章节、映射及输出容器，然后保存为 v6 JSON 预设。
2. 打开 `AB-AV1`，选择该预设和可选的输出目录。
3. 设置目标 VMAF、CRF 范围、采样数量、单段时长、彻底搜索和 VMAF 模型。
4. 点击“添加媒体”或直接把文件拖进页面；点击“开始搜索队列”。
5. 每个搜索成功的任务会显示 CRF/VMAF/预测视频大小，并立即加入 3FUI 原生编码队列。

## 搜索任务管理

- **开始搜索队列**：依次处理所有“等待”任务。
- **停止**：停止所选任务；未选择时作用于当前任务。等待任务直接变为“已停止”，活动任务会取消并结束 `ab-av1` 及其 FFmpeg 子进程。
- **暂停 / 恢复**：只处理所选任务中的“搜索中”和“已暂停”项，等待或结束项不受影响。只要选择中存在搜索中任务，本次操作就是暂停；仅当所有可操作项均已暂停时，本次操作才是恢复。未选择时作用于当前任务。暂停会同时冻结 `ab-av1` 和已经启动的 FFmpeg 子进程。
- **移除**：移除所选的非活动任务。运行中、已暂停或正在停止的任务需先停止。
- **重置状态**：清除所选非活动任务的结果和错误，并恢复为“等待”；若搜索队列仍在运行，会自动重新处理。

任务状态包括：等待、搜索中、已暂停、正在停止、已入队、失败、已停止。

也可以在任务列表中右键：菜单会按当前选择动态提供开始、停止、暂停/恢复、重置和移除操作。右键一个尚未选中的任务会先选中该任务；右键已有选择会保留多选。

## VMAF 模型

模型框支持三种方式：

- 留空：不传 `--vmaf`，完全使用 ab-av1 的自动模型逻辑。
- 内置模型：点击“扫描模型”，或直接输入模型名称，例如 `vmaf_v0.6.1`。
- 本地模型：点击“本地 JSON”，或输入模型 JSON 的完整路径。

扫描逻辑参考 3FUI：先运行当前默认 `ffmpeg -hide_banner -h filter=libvmaf` 读取默认模型，再扫描 FFmpeg 及同目录下的 `avfilter` / `libavfilter` / `libvmaf` 运行库中的内置模型名。内置名称按 `model=version=...` 传给 ab-av1，本地文件按 `model=path=...` 传入；Windows 路径会自动按 FFmpeg 滤镜语法转义。

## 预设参数映射

搜索阶段会映射以下画面参数：

| FFmpegFreeUI 设置 | ab-av1 参数 |
|---|---|
| `libsvtav1` | `--encoder libsvtav1` |
| 编码预设 | `--preset` |
| 像素格式 | `--pix-format` |
| `svtav1-params` 中的 `keyint` | `--keyint` |
| `svtav1-params` 中的 `scd` | `--scd` |
| 其余 SVT 参数 | 每项一个 `--svt key=value` |
| 自定义视频编码参数 | `--enc key=value` |
| 自定义视频滤镜 | `--vfilter` |

原 CRF 不会传入搜索。搜索成功后，插件写入：

- `视频参数_比特率_控制方式 = 1`（CRF）
- `视频参数_质量控制_参数名 = crf`
- `视频参数_质量控制_值 = 搜索结果`

如果 `-svtav1-params` 中手写了 `crf=...`，该值也会同步替换，避免覆盖 FFmpeg 的 `-crf`。音频、字幕、附件、章节、元数据和流映射不参与采样，但仍由最终的 3FUI 原生预设处理。

以下这类 SVT 高级参数会保留并参与 CRF 测试：

```text
-svtav1-params tune=0:keyint=8s:enable-tf=2:enable-cdef=1:
cdef-scaling=12:enable-dlf=2:enable-qm=1:qm-min=4:chroma-qm-min=10:
enable-variance-boost=1:variance-boost-strength=1:tile-columns=0:
tile-rows=0:film-grain=4:film-grain-denoise=1:sharpness=1:ac-bias=1
```

## 构建和测试

要求 Windows 10+ 和 .NET 10 SDK。

```powershell
dotnet restore .\FFmpegFreeUI.AbAv1.vbproj
dotnet build .\FFmpegFreeUI.AbAv1.vbproj -c Release --no-restore
```


## 当前限制

- 当前只支持 `libsvtav1` 和 FFmpegFreeUI v6 JSON 预设。
- 3FUI 内置缩放、裁剪、帧率转换、插帧、降噪、锐化、字幕烧录、色彩转换、帧服务器、剪辑区间等尚未等价映射给 ab-av1；检测到这些设置时会拒绝搜索，避免采样处理链与最终编码不一致。
- 自定义视频滤镜通过 `--vfilter` 传入；改变尺寸、时间轴或帧率的复杂滤镜仍需人工核对。
- ab-av1 的预测大小仅指视频流，最终文件还会包含音频、字幕和附件。
- 暂停/恢复进程树使用 Windows 进程接口，因此插件仅面向 Windows。

## 许可证

项目当前采用 MIT 许可证，但其所引用的 LakeUI 包要求社区版本采用 `GPL 3.0 only` 许可，作者已购买赞助者许可，因此采用了 MIT 许可证发布。若你的 LakeUI 采用了其他许可，应以该许可文本为准，再决定是否调整本项目许可证；本仓库不替授权合同作额外解释。
