# PKG Extractor

**Wallpaper Engine 壁纸包提取工具** —— 单文件、零依赖、免安装 .NET 运行时。

把 `.pkg` / `.mpkg` 拖进窗口，就能把壁纸引擎的壁纸包解开：贴图、着色器、音频、以及**壁纸视频本身**。
壁纸视频支持**直接导出为 MP4**，字节原样拷贝，不重新编码、不裁剪，帧率与时长和作者导出的完全一致。

![图标](assets/icon_preview.png)

---

## 它能做什么

### 导出 MP4（壁纸视频原样取出）

壁纸包里那段视频，一键导出成 `.mp4`：

- **字节原样拷贝** —— 不重新编码、不裁剪、不调时间基，导出文件与包内原视频 SHA-256 完全一致
- 帧率、时间基、时长、音轨全部保持作者编码时的样子，**不会变速**
- 自动按优先级找视频：`project.json` / `scene.json` 里 `file` 指向的条目 → 包内最大的 `.mp4/.webm/.mov` → 从大到小逐个尝试 `.tex` 并解出内嵌 MP4
- 找不到视频会如实写「包内没有视频数据」，不会把静态图硬当视频导出
- 导出后在日志里显示时长、帧率、分辨率、帧数、是否含音轨

### 解包预览（先看后存）

先读到内存，左侧树形列出全部文件，右侧实时预览，勾选想要的再保存：

- **图片直接显示**（含 TEX 转出的 JPG / PNG / GIF）
- **视频内嵌播放** —— 带播放/暂停和进度条，静音、按原速播放，元信息标注真实帧率与帧数
- JSON / 着色器 / 配置文件显示文本内容
- 勾选想保留的文件，**只保存选中的部分**
- 支持全选 / 全不选 / 文件夹级联勾选（三态）

### TEX 贴图解码

Wallpaper Engine 的 `.tex` 是自定义容器，工具完整实现了格式解析：

| 输入 | 输出 |
|---|---|
| 内嵌成品图（JPEG / PNG / BMP …） | 原样直出，不二次压缩 |
| 视频纹理 | `.mp4` |
| GIF 纹理 | 带动画的 `.gif` |
| DXT1 / DXT3 / DXT5 | 无损解压为 `.png` |
| R8 / RG88 / RGBA8888 | `.png` |
| LZ4 压缩的 mipmap | 自动解开后再处理 |

- 支持 TEX 容器版本 TEXB0001 – TEXB0004、帧信息 TEXS0001 – TEXS0003
- 可输出全部 mipmap 层级（默认只出第一层）
- 可保留原始 `.tex` 副本

### 其它

- 拖拽文件或**整个文件夹**到窗口批量入队
- 也可以把 `.pkg` / `.mpkg` 直接拖到 exe 图标上打开
- 每个包单独建子目录、覆盖同名文件等选项
- 逐文件进度、百分比、可取消、可展开日志
- 路径穿越防护、Windows 保留名处理、重名自动加序号

---

## 系统要求

**Windows 10 / 11 x64**。免安装 .NET 运行时（单文件自包含发布）。

---

## 使用

1. 把 `scene.pkg` 或 `*.mpkg` 拖进窗口
2. 选择输出目录（默认 `文档\PKG Extractor Output`）
3. **只要视频** → 点「导出 MP4」，每个包导出一个 `.mp4`
4. **想先看看** → 点「预览」，勾选要的文件后点「保存选中」
5. **全部要** → 点「开始解包」

---

## 从源码构建

需要 [.NET 11 SDK](https://dotnet.microsoft.com/download)。

```powershell
cd src
dotnet publish -c Release -o ../dist
```

产物是单个 `dist\PkgExtractor.exe`，约 130 MB（自包含，内含运行时）。

> **两个 WPF 单文件发布的坑**（已经在 csproj 里处理好，改配置时别踩回去）：
> - `IncludeAllContentForSelfExtract=true` 是必须的 —— WPF 的 `DirectWriteForwarder.dll` 等原生伴随文件必须被抽到磁盘，否则启动即 `BadImageFormatException`。
> - `InvariantGlobalization` 必须是 `false` —— WPF 字体缓存初始化时会构造 `CultureInfo("en")`，不变全球化模式下会直接抛 `CultureNotFoundException`。

### 跑自检

内置自检覆盖 LZ4 / DXT1 / DXT5 / PKG 往返 / TEX 静态图 / TEX GIF / 预览选择性保存 / MP4 导出。
传一个真实壁纸包路径即可带上集成用例：

```powershell
cd tools/Selftest
dotnet run -c Release -- "D:\Steam\steamapps\workshop\content\431960\1706509671\scene.pkg"
```

输出形如 `===== 74 passed, 0 failed =====`，退出码 0 表示全部通过。

---

## 项目结构

```
src/
  App.xaml(.cs)              应用入口，带全局异常兜底
  MainWindow.xaml(.cs)       主窗口：队列、解包、导出 MP4、日志
  PreviewWindow.xaml(.cs)    预览窗口：树形勾选、图片/文本/视频预览
  Core/
    PkgReader.cs             PKG / MPKG 容器解析（PKGM0014 等）
    TexReader.cs             TEX 容器解析（TEXV/TEXI/TEXB/TEXS）
    TexEnums.cs              TEX 格式与标志位枚举
    TexImageConverter.cs     mipmap 解压、内嵌文件识别、文件扩展名映射
    TexConverter.cs          TEX → 成品文件（内存版）
    ExtractionService.cs     解包落盘、路径安全、重名处理
    PreviewService.cs        预览数据模型与选择性保存
    Mp4Probe.cs              手写 ISO BMFF 解析，读时长/帧率/分辨率/编码
    Mp4Exporter.cs           在包里定位并取出壁纸视频
    Lz4.cs                   LZ4 块解压
    Dxt.cs                   DXT1 / DXT3 / DXT5 (S3TC) 解压
tools/
  IconGen/                   图标生成器（自写 PNG 编码 + ICO 打包）
  Selftest/                  自检套件
```

**零第三方依赖**：LZ4、DXT、PNG/ICO 编码、ISO BMFF 解析全部内置实现，`dotnet publish` 时不需要拉任何 NuGet 包。

---

## 说明

格式规范参考 [notscuffed/repkg](https://github.com/notscuffed/repkg) 自行实现，未直接引用其代码。

仅供备份与个人素材提取使用。壁纸的版权归原作者所有，请勿用于二次分发或商业用途。
